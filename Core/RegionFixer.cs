using System.Diagnostics;
using System.IO.Compression;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;

namespace FuckPCL;

public sealed record Inspection(string Path, string State, string Sha256, string Assembly,
    string Method, long FileOffset, int OriginalMethodSize, bool Compressed);

public static class RegionFixer
{
    public const string MethodName = "PCL.Core.App.Region.RegionAssessment.get_IsRestricted";
    private const int MaxFileSize = 512 * 1024 * 1024;
    private static readonly byte[] BundleSignature = Convert.FromHexString(
        "8B1202B96A612038727B930214D7A03213F5B9E6EFAE3318EE3B2DCE24B36AAE");

    private sealed record Entry(long Offset, long Size, long CompressedSize, string Name);
    private sealed record Located(Entry Entry, byte[] Assembly, int MethodOffset, int MethodSize, bool Patched);

    public static string Hash(byte[] data) => Convert.ToHexString(SHA256.HashData(data));

    public static Inspection Inspect(string path)
    {
        path = Path.GetFullPath(path);
        var bytes = ReadTarget(path);
        var located = Locate(bytes);
        if (!located.Patched && located.Entry.CompressedSize != 0)
            throw new NotSupportedException("此文件的核心程序集经过压缩，目前仅支持未压缩的 PCL-CE 单文件版本，未修改文件。");
        return new(path, located.Patched ? "已修复" : "可修复", Hash(bytes), located.Entry.Name,
            MethodName, located.Entry.Offset + located.MethodOffset, located.MethodSize,
            located.Entry.CompressedSize != 0);
    }

    public static byte[] ExtractCore(byte[] file) => Locate(file).Assembly;

    public static byte[] CreatePatchedCopy(byte[] original)
    {
        var located = Locate(original);
        if (located.Patched) return (byte[])original.Clone();
        if (located.Entry.CompressedSize != 0)
            throw new NotSupportedException("此文件的核心程序集经过压缩，目前仅支持未压缩的 PCL-CE 单文件版本，未修改文件。");

        var result = (byte[])original.Clone();
        var offset = checked((int)located.Entry.Offset + located.MethodOffset);
        // A complete tiny IL method: header(CodeSize=2), ldc.i4.0, ret.
        // Replacing the whole method header also removes old local/EH metadata.
        result.AsSpan(offset, located.MethodSize).Clear();
        result[offset] = 0x0a;
        result[offset + 1] = 0x16;
        result[offset + 2] = 0x2a;
        var verified = Locate(result);
        if (!verified.Patched || result.Length != original.Length)
            throw new InvalidDataException("修复结果校验失败。");
        for (var i = 0; i < result.Length; i++)
            if ((i < offset || i >= offset + located.MethodSize) && result[i] != original[i])
                throw new InvalidDataException("检测到目标方法以外的内容发生变化。");
        return result;
    }

    public static Inspection Apply(string path)
    {
        path = Path.GetFullPath(path);
        EnsureStopped(path);
        var original = ReadTarget(path);
        var located = Locate(original);
        if (located.Patched) return Inspect(path);
        var patched = CreatePatchedCopy(original);
        var beforeHash = Hash(original);
        var afterHash = Hash(patched);
        AtomicReplace(path, patched, beforeHash);
        var written = ReadTarget(path);
        if (Hash(written) != afterHash || !Locate(written).Patched)
            throw new IOException("写入后的校验未通过，请手动换回自行保存的原文件或重新下载启动器。");
        return Inspect(path);
    }

    private static void AtomicReplace(string path, byte[] replacement, string expectedHash)
    {
        var temporary = path + ".regionfix-" + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(replacement);
                stream.Flush(true);
            }
            if (Hash(File.ReadAllBytes(temporary)) != Hash(replacement))
                throw new IOException("临时文件写入校验失败。");
            EnsureStopped(path);
            // Deny concurrent writers/deletion while validating the current snapshot.
            using (var current = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                if (Convert.ToHexString(SHA256.HashData(current)) != expectedHash)
                    throw new IOException("操作过程中 EXE 已改变，请重新检测。");
            File.Replace(temporary, path, null);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static byte[] ReadTarget(string path)
    {
        if (!string.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("请选择 PCL-CE 的 .exe 文件。");
        var info = new FileInfo(path);
        if (!info.Exists) throw new FileNotFoundException("找不到所选 EXE。", path);
        if (info.Length < 4096 || info.Length > MaxFileSize)
            throw new InvalidDataException("EXE 大小不在支持范围内（4 KB～512 MB）。");
        if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new IOException("请选择实际的 EXE 文件，不支持符号链接。");
        return File.ReadAllBytes(path);
    }

    private static void EnsureStopped(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        foreach (var process in Process.GetProcessesByName(name))
        {
            using (process)
            {
                try
                {
                    if (string.Equals(process.MainModule?.FileName, path, StringComparison.OrdinalIgnoreCase))
                        throw new IOException("PCL-CE 正在运行，请先退出启动器再操作。");
                }
                catch (System.ComponentModel.Win32Exception)
                {
                    throw new IOException("检测到同名进程但无法确认其路径，请先退出该启动器。");
                }
                catch (InvalidOperationException) { /* Process has already exited. */ }
            }
        }
    }

    private static Located Locate(byte[] file)
    {
        if (file.Length < 64 || file[0] != 'M' || file[1] != 'Z')
            throw new InvalidDataException("不是有效的 Windows EXE。");
        var entries = ReadBundle(file);
        var matches = entries.Where(e => string.Equals(e.Name, "PCL.Core.dll", StringComparison.Ordinal)).ToArray();
        if (matches.Length != 1)
            throw new NotSupportedException("未找到唯一的 PCL.Core.dll；仅支持包含此核心库的 PCL-CE 单文件版本。");
        var entry = matches[0];
        var assembly = ReadEntry(file, entry);
        using var pe = new PEReader(new MemoryStream(assembly, false));
        if (!pe.HasMetadata || pe.PEHeaders.CorHeader is not { } cor)
            throw new InvalidDataException("PCL.Core.dll 不含托管元数据。");
        if (cor.ManagedNativeHeaderDirectory.Size != 0 || cor.StrongNameSignatureDirectory.Size != 0)
            throw new NotSupportedException("不支持预编译或强名称签名的核心库，未修改文件。");
        var md = pe.GetMetadataReader();
        if (md.GetString(md.GetAssemblyDefinition().Name) != "PCL.Core")
            throw new InvalidDataException("程序集名称不匹配。");
        var types = md.TypeDefinitions.Where(handle =>
        {
            var type = md.GetTypeDefinition(handle);
            return md.GetString(type.Namespace) == "PCL.Core.App.Region" && md.GetString(type.Name) == "RegionAssessment";
        }).ToArray();
        if (types.Length != 1)
            throw new NotSupportedException("此版本不存在已知的地区限制判断，未修改文件。");
        var methods = md.GetTypeDefinition(types[0]).GetMethods().Where(handle =>
            md.GetString(md.GetMethodDefinition(handle).Name) == "get_IsRestricted").ToArray();
        if (methods.Length != 1) throw new InvalidDataException("目标方法不唯一或不存在。");
        var method = md.GetMethodDefinition(methods[0]);
        if (!md.GetBlobBytes(method.Signature).AsSpan().SequenceEqual(new byte[] { 0x20, 0x00, 0x02 }))
            throw new NotSupportedException("限制判断的方法签名已改变，未修改文件。");
        if (method.RelativeVirtualAddress == 0) throw new InvalidDataException("目标方法没有 IL 方法体。");
        var body = pe.GetMethodBody(method.RelativeVirtualAddress);
        if (body.ExceptionRegions.Length != 0)
            throw new NotSupportedException("目标方法包含异常处理区，未修改文件。");
        var il = body.GetILBytes() ?? throw new InvalidDataException("无法读取 IL。");
        var offset = RvaToOffset(pe.PEHeaders, method.RelativeVirtualAddress);
        if (body.Size < 3 || offset < 0 || offset > assembly.Length - body.Size)
            throw new InvalidDataException("方法体范围无效。");
        return new(entry, assembly, offset, body.Size,
            il.AsSpan().SequenceEqual(new byte[] { 0x16, 0x2a }));
    }

    private static int RvaToOffset(PEHeaders headers, int rva)
    {
        foreach (var section in headers.SectionHeaders)
        {
            var delta = (long)rva - section.VirtualAddress;
            if (delta >= 0 && delta < section.SizeOfRawData)
                return checked(section.PointerToRawData + (int)delta);
        }
        throw new InvalidDataException("方法 RVA 不在文件节内。");
    }

    private static List<Entry> ReadBundle(byte[] file)
    {
        using (var pe = new PEReader(new MemoryStream(file, false)))
        {
            if (pe.PEHeaders.PEHeader is not { } header || pe.HasMetadata)
                throw new NotSupportedException("请选择 PCL-CE 的单文件 EXE。");
            if (header.CertificateTableDirectory.Size != 0)
                throw new NotSupportedException("此 EXE 带有数字签名，工具不修改已签名文件。");
        }
        var signatureOffset = file.AsSpan().IndexOf(BundleSignature);
        if (signatureOffset < 8) throw new NotSupportedException("未找到 .NET 单文件包标记。");
        var headerOffset = BitConverter.ToInt64(file, signatureOffset - 8);
        if (headerOffset < signatureOffset + BundleSignature.Length || headerOffset > file.Length - 16)
            throw new InvalidDataException("单文件包头偏移无效。");
        using var reader = new BinaryReader(new MemoryStream(file, false));
        reader.BaseStream.Position = headerOffset;
        var major = reader.ReadUInt32();
        var minor = reader.ReadUInt32();
        var count = reader.ReadInt32();
        if (major != 6 || minor != 0 || count < 1 || count > 10000)
            throw new NotSupportedException("仅支持 .NET 6+ 的 6.0 单文件包格式。");
        _ = ReadString(reader);
        reader.BaseStream.Position = checked(reader.BaseStream.Position + 32);
        var flags = reader.ReadUInt64();
        if (flags != 0)
            throw new NotSupportedException("此单文件包使用不同的提取模式，当前工具不支持，未修改文件。");
        var entries = new List<Entry>();
        for (var i = 0; i < count; i++)
        {
            var offset = reader.ReadInt64();
            var size = reader.ReadInt64();
            var compressed = reader.ReadInt64();
            _ = reader.ReadByte();
            var name = ReadString(reader);
            var stored = compressed == 0 ? size : compressed;
            if (offset < 0 || size < 0 || size > MaxFileSize || compressed < 0 ||
                stored > file.Length || offset > headerOffset - stored)
                throw new InvalidDataException("单文件包条目范围无效。");
            entries.Add(new(offset, size, compressed, name));
        }
        var ranges = entries.Where(e => (e.CompressedSize == 0 ? e.Size : e.CompressedSize) > 0)
            .OrderBy(e => e.Offset).ToArray();
        for (var i = 1; i < ranges.Length; i++)
            if (ranges[i].Offset < ranges[i - 1].Offset +
                (ranges[i - 1].CompressedSize == 0 ? ranges[i - 1].Size : ranges[i - 1].CompressedSize))
                throw new InvalidDataException("包条目互相重叠。");
        return entries;
    }

    private static string ReadString(BinaryReader reader)
    {
        var length = reader.Read7BitEncodedInt();
        if (length < 0 || length > 32768 || length > reader.BaseStream.Length - reader.BaseStream.Position)
            throw new InvalidDataException("包内字符串长度无效。");
        return System.Text.Encoding.UTF8.GetString(reader.ReadBytes(length));
    }

    private static byte[] ReadEntry(byte[] file, Entry entry)
    {
        if (entry.CompressedSize == 0)
            return file.AsSpan(checked((int)entry.Offset), checked((int)entry.Size)).ToArray();
        using var input = new MemoryStream(file, checked((int)entry.Offset), checked((int)entry.CompressedSize));
        using var deflate = new DeflateStream(input, CompressionMode.Decompress);
        var output = new byte[checked((int)entry.Size)];
        deflate.ReadExactly(output);
        if (deflate.ReadByte() != -1) throw new InvalidDataException("解压后的程序集超出声明长度。");
        return output;
    }
}
