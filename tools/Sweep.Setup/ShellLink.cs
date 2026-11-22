using System.Reflection;
using System.Runtime.InteropServices;

namespace Sweep.Setup;

/// <summary>用系统自带的 WScript.Shell 创建 .lnk，避免引入第三方 COM 依赖。</summary>
internal static class ShellLink
{
    public static void Create(string linkPath, string target, string? arguments, string? icon, string description)
    {
        var dir = Path.GetDirectoryName(linkPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        object shell = Activator.CreateInstance(
            Type.GetTypeFromProgID("WScript.Shell")
            ?? throw new InvalidOperationException("系统缺少 WScript.Shell 组件，无法创建快捷方式。"))!;
        object? link = null;
        try
        {
            link = Invoke(shell, "CreateShortcut", linkPath)!;
            Set(link, "TargetPath", target);
            if (arguments is not null) Set(link, "Arguments", arguments);
            if (icon is not null) Set(link, "IconLocation", icon);
            Set(link, "Description", description);
            Invoke(link, "Save");
        }
        finally
        {
            if (link is not null) Marshal.ReleaseComObject(link);
            Marshal.ReleaseComObject(shell);
        }
    }

    public static void Delete(string linkPath)
    {
        if (File.Exists(linkPath)) File.Delete(linkPath);
    }

    private static object? Invoke(object target, string name, params object[] args) =>
        target.GetType().InvokeMember(name, BindingFlags.InvokeMethod, null, target, args);

    private static void Set(object target, string name, object value) =>
        target.GetType().InvokeMember(name, BindingFlags.SetProperty, null, target, new[] { value });
}
