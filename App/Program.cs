using System.Diagnostics;
using System.IO;
using FuckPCL;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm(args.FirstOrDefault()));
    }
}

internal sealed class MainForm : Form
{
    private const string ProjectUrl = "https://github.com/isakiooo/FuckPCL";
    private readonly TextBox pathBox = new() { Dock = DockStyle.Fill, PlaceholderText = "选择或拖入 PCL-CE 的 EXE 文件…" };
    private readonly Button browse = new() { Text = "选择文件…", AutoSize = true };
    private readonly Button inspect = new() { Text = "检测", Width = 100, Height = 40 };
    private readonly Button apply = new() { Text = "恢复下载功能", Width = 166, Height = 40 };
    private readonly Button project = new() { Text = "打开原项目", Width = 140, Height = 40 };
    private readonly Label status = new() { Text = "等待选择文件", AutoSize = true };
    private readonly TextBox log = new()
    {
        Dock = DockStyle.Fill, Multiline = true, ReadOnly = true,
        ScrollBars = ScrollBars.Vertical, BorderStyle = BorderStyle.FixedSingle,
        BackColor = Color.White, Font = new Font("Microsoft YaHei UI", 9)
    };
    private bool busy;

    public MainForm(string? initialPath)
    {
        Text = "FuckPCL";
        Font = new Font("Microsoft YaHei UI", 10);
        BackColor = Color.FromArgb(245, 247, 251);
        ForeColor = Color.FromArgb(30, 42, 61);
        ClientSize = new Size(750, 500);
        MinimumSize = new Size(670, 480);
        StartPosition = FormStartPosition.CenterScreen;
        AllowDrop = true;
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 8, Padding = new Padding(28)
        };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 45));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 49));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 63));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 33));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 33));
        layout.Controls.Add(new Label
        {
            Text = "FuckPCL", AutoSize = true,
            Font = new Font("Microsoft YaHei UI", 19, FontStyle.Bold)
        }, 0, 0);
        layout.Controls.Add(new Label
        {
            Text = "修复本地程序的地区限制，让下载、文件补全和整合包安装恢复可用。\n直接修改所选 EXE；不自动备份，不提供还原。", AutoSize = true
        }, 0, 1);
        var selector = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
        selector.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        selector.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
        selector.Controls.Add(pathBox, 0, 0);
        selector.Controls.Add(browse, 1, 0);
        layout.Controls.Add(selector, 0, 2);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(0, 5, 0, 0) };
        apply.BackColor = Color.FromArgb(35, 103, 215);
        apply.ForeColor = Color.White;
        apply.FlatStyle = FlatStyle.Flat;
        apply.FlatAppearance.BorderSize = 0;
        buttons.Controls.AddRange([inspect, apply, project]);
        layout.Controls.Add(buttons, 0, 3);
        layout.Controls.Add(status, 0, 4);
        layout.Controls.Add(log, 0, 5);
        layout.Controls.Add(new Label
        {
            Text = "免费开源，认准原项目。倒卖司马！良心已被 GC 回收。",
            AutoSize = true, ForeColor = Color.FromArgb(172, 48, 48),
            Font = new Font("Microsoft YaHei UI", 9, FontStyle.Bold),
            Padding = new Padding(0, 7, 0, 0)
        }, 0, 6);
        layout.Controls.Add(new Label { Text = "操作前请退出 PCL-CE，并自行复制保存原 EXE。", AutoSize = true, Padding = new Padding(0, 7, 0, 0) }, 0, 7);
        Controls.Add(layout);
        browse.Click += (_, _) =>
        {
            using var dialog = new OpenFileDialog { Filter = "PCL-CE 程序 (*.exe)|*.exe", CheckFileExists = true };
            if (File.Exists(pathBox.Text)) dialog.FileName = pathBox.Text;
            if (dialog.ShowDialog(this) == DialogResult.OK) pathBox.Text = dialog.FileName;
        };
        inspect.Click += async (_, _) => await RunOperation("检测", RegionFixer.Inspect);
        apply.Click += async (_, _) => await RunOperation("恢复下载功能", RegionFixer.Apply);
        project.Click += (_, _) =>
        {
            try
            {
                Process.Start(new ProcessStartInfo(ProjectUrl) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                status.ForeColor = Color.FromArgb(172, 48, 48);
                status.Text = "无法打开浏览器";
                log.Text = $"请手动访问原项目：{ProjectUrl}\r\n\r\n{ex.Message}";
            }
        };
        DragEnter += (_, e) => { if (!busy && e.Data?.GetDataPresent(DataFormats.FileDrop) == true) e.Effect = DragDropEffects.Copy; };
        DragDrop += (_, e) =>
        {
            if (!busy && e.Data?.GetData(DataFormats.FileDrop) is string[] { Length: 1 } paths) pathBox.Text = paths[0];
        };
        FormClosing += (_, e) => { if (busy) e.Cancel = true; };
        if (!string.IsNullOrWhiteSpace(initialPath)) pathBox.Text = initialPath;
        log.Text = "支持含有对应地区限制模块的 PCL-CE 单文件版本。\r\n选择文件后点击“检测”查看是否适用。";
    }

    private async Task RunOperation(string action, Func<string, Inspection> operation)
    {
        if (busy) return;
        if (string.IsNullOrWhiteSpace(pathBox.Text))
        {
            status.Text = "请先选择 PCL-CE 的 EXE 文件。";
            return;
        }
        var path = pathBox.Text.Trim().Trim('"');
        busy = true;
        foreach (var control in new Control[] { pathBox, browse, inspect, apply }) control.Enabled = false;
        UseWaitCursor = true;
        status.Text = action + "中…";
        try
        {
            var result = await Task.Run(() => operation(path));
            status.ForeColor = Color.FromArgb(30, 117, 73);
            status.Text = action + "完成 · " + result.State;
            log.Text = $"文件：{result.Path}\r\n状态：{result.State}\r\nSHA-256：{result.Sha256}\r\n";
            if (result.State == "已修复")
                log.AppendText("\r\n可以启动 PCL-CE 使用下载、补全和整合包安装功能。\r\n启动器更新后请重新检测；新版若改变实现，工具会提示不支持。");
        }
        catch (Exception ex)
        {
            status.ForeColor = Color.FromArgb(172, 48, 48);
            status.Text = action + "未完成";
            log.Text = ex.Message;
        }
        finally
        {
            busy = false;
            UseWaitCursor = false;
            foreach (var control in new Control[] { pathBox, browse, inspect, apply }) control.Enabled = true;
        }
    }
}
