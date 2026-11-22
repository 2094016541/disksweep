namespace DiskSweep.App.Services;

/// <summary>
/// 主题三态的纯策略，不依赖 WPF，可在无 UI 的测试进程里直接验证。
/// 只有 light / dark 两种实际皮肤；system 是按 Windows 偏好解析出来的第三种「偏好」。
/// </summary>
public static class ThemeModes
{
    public const string System = "system";
    public const string Light = "light";
    public const string Dark = "dark";

    public static string Normalize(string? mode) => mode switch
    {
        Light => Light,
        Dark => Dark,
        _ => System
    };

    /// <summary>主界面一键切换的循环顺序：跟随系统 → 浅色 → 深色 → 跟随系统。</summary>
    public static string Next(string? mode) => Normalize(mode) switch
    {
        System => Light,
        Light => Dark,
        _ => System
    };

    public static string Label(string? mode) => Normalize(mode) switch
    {
        Light => "浅色",
        Dark => "深色",
        _ => "跟随系统"
    };

    public static int ToIndex(string? mode) => Normalize(mode) switch
    {
        Light => 1,
        Dark => 2,
        _ => 0
    };

    public static string FromIndex(int index) => index switch
    {
        1 => Light,
        2 => Dark,
        _ => System
    };
}
