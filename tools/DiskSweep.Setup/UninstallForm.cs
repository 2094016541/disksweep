using System.Diagnostics;
using Microsoft.Win32;

namespace DiskSweep.Setup;

internal sealed class UninstallForm : Form
{
    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\DiskSweep";
    private const string LinkName = "DiskSweep.lnk";

    private readonly string _installDir;
    private readonly Label _pathLabel = new();
    private readonly CheckBox _keepData = new();
    private readonly Button _uninstallButton = new();

    public UninstallForm()
    {
        _installDir = ReadInstallLocation() ?? "";

        Text = "卸载 DiskSweep";
        ClientSize = new System.Drawing.Size(560, 280);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Microsoft YaHei UI", 9F);

        Controls.Add(new Label
        {
            Text = "卸载 DiskSweep",
            Font = new Font("Microsoft YaHei UI", 18F, FontStyle.Bold),
            Location = new Point(28, 20), Size = new Size(320, 36)
        });

        Controls.Add(new Label
        {
            Text = "安装目录：", Location = new Point(28, 72), Size = new Size(80, 22)
        });
        _pathLabel.Text = string.IsNullOrEmpty(_installDir) ? "未找到安装信息" : _installDir;
        _pathLabel.ForeColor = Color.Gray;
        _pathLabel.AutoEllipsis = true;
        _pathLabel.Location = new Point(108, 72);
        _pathLabel.Size = new Size(420, 22);
        Controls.Add(_pathLabel);

        _keepData.Text = "保留设置与缓存（移回 %LOCALAPPDATA%\\DiskSweep）";
        _keepData.Location = new Point(28, 112);
        _keepData.Size = new Size(500, 24);
        Controls.Add(_keepData);

        Controls.Add(new Label
        {
            Text = "不勾选则同时删除程序文件、数据库、设置与缓存。",
            ForeColor = Color.Gray, Location = new Point(28, 140), Size = new Size(500, 22)
        });

        var cancel = new Button { Text = "取消", Location = new Point(292, 226), Size = new Size(110, 32) };
        cancel.Click += (_, _) => Close();
        Controls.Add(cancel);

        _uninstallButton.Text = "卸载";
        _uninstallButton.Location = new Point(416, 226);
        _uninstallButton.Size = new Size(110, 32);
        _uninstallButton.Enabled = !string.IsNullOrEmpty(_installDir) && Directory.Exists(_installDir);
        _uninstallButton.Click += Uninstall_Click;
        Controls.Add(_uninstallButton);
    }

    private static string? ReadInstallLocation()
    {
        using var key = Registry.CurrentUser.OpenSubKey(UninstallKey);
        return key?.GetValue("InstallLocation") as string;
    }

    private void Uninstall_Click(object? sender, EventArgs e)
    {
        if (Process.GetProcessesByName("DiskSweep.App").Length > 0)
        {
            MessageBox.Show(this, "DiskSweep 正在运行，请先关闭它再卸载。", "无法卸载",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var answer = MessageBox.Show(this,
            _keepData.Checked
                ? "将删除程序文件，并把设置与缓存移回 %LOCALAPPDATA%\\DiskSweep。确认卸载？"
                : "将删除程序文件以及全部设置、数据库与缓存，此操作不可撤销。确认卸载？",
            "确认卸载", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
        if (answer != DialogResult.Yes) return;

        try
        {
            if (_keepData.Checked) MigrateData();
            RemoveShortcuts();
            Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false);
            ScheduleDirectoryDeletion(_installDir);

            MessageBox.Show(this, "卸载已启动，程序文件将在几秒后自动删除。", "卸载完成",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "卸载失败：" + ex.Message, "卸载失败",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void MigrateData()
    {
        var source = Path.Combine(_installDir, "data");
        if (!Directory.Exists(source)) return;
        var target = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DiskSweep");
        CopyDirectory(source, target);
    }

    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.GetFiles(source))
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)), true);
        foreach (var dir in Directory.GetDirectories(source))
            CopyDirectory(dir, Path.Combine(target, Path.GetFileName(dir)));
    }

    private static void RemoveShortcuts()
    {
        ShellLink.Delete(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), LinkName));
        ShellLink.Delete(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "DiskSweep", LinkName));
    }

    /// <summary>卸载器自身位于安装目录内，不能删除正在运行的自己，交给延迟启动的 cmd 完成清理。</summary>
    private static void ScheduleDirectoryDeletion(string dir)
    {
        var script = Path.Combine(Path.GetTempPath(), "disksweep-uninstall-" + Guid.NewGuid().ToString("N") + ".cmd");
        File.WriteAllText(script,
            "@echo off\r\n" +
            "ping -n 3 127.0.0.1 >nul\r\n" +
            $"rmdir /s /q \"{dir}\"\r\n" +
            $"del \"{script}\"\r\n");
        Process.Start(new ProcessStartInfo("cmd.exe", $"/c \"{script}\"")
        {
            UseShellExecute = true,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        });
    }
}
