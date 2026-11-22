using System.Diagnostics;
using System.IO.Compression;
using Microsoft.Win32;

namespace Sweep.Setup;

internal sealed class InstallerForm : Form
{
    private const string AppExe = "Sweep.App.exe";
    private const string UninstallExe = "Uninstall.exe";
    private const string PayloadResource = "Sweep.Setup.payload.zip";
    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\Sweep";
    private const string LinkName = "Sweep.lnk";

    private readonly TextBox _directory = new();
    private readonly CheckBox _desktopShortcut = new();
    private readonly CheckBox _startMenuShortcut = new();
    private readonly Button _installButton = new();
    private readonly Button _browseButton = new();
    private readonly Button _cancelButton = new();
    private readonly ProgressBar _progress = new();
    private readonly Label _status = new();

    public InstallerForm()
    {
        Text = "Sweep 安装程序";
        ClientSize = new System.Drawing.Size(580, 414);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Microsoft YaHei UI", 9F);

        Controls.Add(new Label
        {
            Text = "Sweep",
            Font = new Font("Microsoft YaHei UI", 20F, FontStyle.Bold),
            Location = new Point(28, 18), Size = new Size(320, 40)
        });
        Controls.Add(new Label
        {
            Text = "磁盘空间分析与清理工具 · 版本 1.0.0",
            ForeColor = Color.Gray,
            Location = new Point(30, 60), Size = new Size(420, 22)
        });

        var locationGroup = new GroupBox
        {
            Text = "安装位置", Location = new Point(24, 96), Size = new Size(532, 116)
        };
        locationGroup.Controls.Add(new Label
        {
            Text = "目标目录：", Location = new Point(16, 28), Size = new Size(80, 22)
        });
        _directory.Location = new Point(16, 52);
        _directory.Size = new Size(388, 26);
        _directory.Text = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Sweep");
        locationGroup.Controls.Add(_directory);
        _browseButton.Text = "浏览…";
        _browseButton.Location = new Point(412, 50);
        _browseButton.Size = new Size(104, 28);
        _browseButton.Click += Browse_Click;
        locationGroup.Controls.Add(_browseButton);
        locationGroup.Controls.Add(new Label
        {
            Text = "程序文件与运行时数据（数据库、设置、缓存）都会保存在此目录内。",
            ForeColor = Color.Gray, Location = new Point(16, 86), Size = new Size(500, 22)
        });
        Controls.Add(locationGroup);

        var shortcutGroup = new GroupBox
        {
            Text = "快捷方式", Location = new Point(24, 224), Size = new Size(532, 78)
        };
        _desktopShortcut.Text = "创建桌面快捷方式";
        _desktopShortcut.Checked = true;
        _desktopShortcut.Location = new Point(16, 24);
        _desktopShortcut.Size = new Size(240, 22);
        _startMenuShortcut.Text = "创建开始菜单快捷方式";
        _startMenuShortcut.Checked = true;
        _startMenuShortcut.Location = new Point(16, 50);
        _startMenuShortcut.Size = new Size(240, 22);
        shortcutGroup.Controls.Add(_desktopShortcut);
        shortcutGroup.Controls.Add(_startMenuShortcut);
        Controls.Add(shortcutGroup);

        _progress.Location = new Point(24, 320);
        _progress.Size = new Size(532, 14);
        _progress.Maximum = 100;
        Controls.Add(_progress);

        _status.Text = "准备就绪。";
        _status.ForeColor = Color.Gray;
        _status.Location = new Point(24, 340);
        _status.Size = new Size(532, 22);
        Controls.Add(_status);

        _cancelButton.Text = "取消";
        _cancelButton.Location = new Point(264, 372);
        _cancelButton.Size = new Size(120, 32);
        _cancelButton.Click += (_, _) => Close();
        Controls.Add(_cancelButton);

        _installButton.Text = "安装";
        _installButton.Location = new Point(400, 372);
        _installButton.Size = new Size(156, 32);
        _installButton.Click += Install_Click;
        Controls.Add(_installButton);
    }

    private void Browse_Click(object? sender, EventArgs e)
    {
        using var dialog = new FolderBrowserDialog { Description = "选择 Sweep 的安装目录" };
        var current = _directory.Text.Trim();
        if (Directory.Exists(current)) dialog.SelectedPath = current;
        if (dialog.ShowDialog(this) == DialogResult.OK) _directory.Text = dialog.SelectedPath;
    }

    private async void Install_Click(object? sender, EventArgs e)
    {
        var dir = _directory.Text.Trim();
        if (!TryValidateDirectory(dir, out var error))
        {
            MessageBox.Show(this, error, "无法安装", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        _installButton.Enabled = false;
        _browseButton.Enabled = false;
        _cancelButton.Enabled = false;
        _status.ForeColor = Color.Gray;

        try
        {
            _status.Text = "正在释放程序文件…";
            await Task.Run(() => InstallFiles(dir));

            _status.Text = "正在创建快捷方式与卸载项…";
            CreateShortcuts(dir);
            WriteUninstallRegistry(dir);

            _progress.Value = 100;
            _status.Text = "安装完成。";
            var answer = MessageBox.Show(this, "Sweep 已安装完成。是否立即启动？", "安装完成",
                MessageBoxButtons.YesNo, MessageBoxIcon.Information, MessageBoxDefaultButton.Button1);
            if (answer == DialogResult.Yes)
            {
                Process.Start(new ProcessStartInfo(Path.Combine(dir, AppExe))
                {
                    UseShellExecute = true, WorkingDirectory = dir
                });
            }
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (Exception ex)
        {
            _status.ForeColor = Color.Firebrick;
            _status.Text = "安装失败。";
            MessageBox.Show(this, "安装失败：" + ex.Message, "安装失败",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            _installButton.Enabled = true;
            _browseButton.Enabled = true;
            _cancelButton.Enabled = true;
        }
    }

    private static bool TryValidateDirectory(string dir, out string error)
    {
        error = "";
        if (string.IsNullOrWhiteSpace(dir))
        {
            error = "请选择安装目录。";
            return false;
        }
        if (!Path.IsPathFullyQualified(dir))
        {
            error = "安装目录必须是完整路径，例如 D:\\Sweep。";
            return false;
        }

        var full = Path.GetFullPath(dir);
        var root = Path.GetPathRoot(full);
        if (root is not null && string.Equals(full.TrimEnd('\\', '/'), root.TrimEnd('\\', '/'),
                StringComparison.OrdinalIgnoreCase))
        {
            error = "不能直接安装到磁盘根目录，请选择一个子文件夹。";
            return false;
        }

        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (!string.IsNullOrEmpty(windows) && full.StartsWith(windows + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
        {
            error = "不能安装到 Windows 系统目录。";
            return false;
        }
        return true;
    }

    private void InstallFiles(string dir)
    {
        var root = Path.GetFullPath(dir);
        Directory.CreateDirectory(root);

        using var stream = typeof(Program).Assembly.GetManifestResourceStream(PayloadResource)
            ?? throw new InvalidOperationException("安装包内缺少应用文件（payload.zip）。");
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);

        int total = Math.Max(1, zip.Entries.Count);
        int index = 0;
        foreach (var entry in zip.Entries)
        {
            var target = Path.GetFullPath(Path.Combine(root, entry.FullName));
            if (!target.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(target, root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"安装包包含非法路径：{entry.FullName}");

            if (entry.FullName.EndsWith('/'))
            {
                Directory.CreateDirectory(target);
            }
            else
            {
                var parent = Path.GetDirectoryName(target);
                if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
                entry.ExtractToFile(target, true);
            }
            ReportProgress(Math.Min(95, ++index * 95 / total));
        }

        // 应用据此把数据库/设置/缓存写入安装目录，而不是 %LOCALAPPDATA%。
        File.WriteAllText(Path.Combine(root, "portable.mode"), "installed\n");

        var self = Environment.ProcessPath;
        if (!string.IsNullOrEmpty(self))
            File.Copy(self, Path.Combine(root, UninstallExe), true);
    }

    private void ReportProgress(int value)
    {
        if (IsHandleCreated && !IsDisposed)
            BeginInvoke(() => _progress.Value = Math.Clamp(value, 0, 100));
    }

    private void CreateShortcuts(string dir)
    {
        var exe = Path.Combine(dir, AppExe);
        var icon = $"\"{exe}\",0";

        if (_desktopShortcut.Checked)
            ShellLink.Create(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), LinkName),
                exe, null, icon, "Sweep 磁盘空间分析与清理");

        if (_startMenuShortcut.Checked)
            ShellLink.Create(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs),
                "Sweep", LinkName), exe, null, icon, "Sweep 磁盘空间分析与清理");
    }

    private static void WriteUninstallRegistry(string dir)
    {
        using var key = Registry.CurrentUser.CreateSubKey(UninstallKey, true)
            ?? throw new InvalidOperationException("无法写入卸载注册表项。");
        key.SetValue("DisplayName", "Sweep");
        key.SetValue("DisplayVersion", "1.0.0");
        key.SetValue("Publisher", "Sweep");
        key.SetValue("InstallLocation", dir);
        key.SetValue("UninstallString", $"\"{Path.Combine(dir, UninstallExe)}\" --uninstall");
        key.SetValue("DisplayIcon", $"\"{Path.Combine(dir, AppExe)}\",0");
        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
    }
}
