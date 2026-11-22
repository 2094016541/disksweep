using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sweep.Ai.Abstractions;
using Sweep.App.Services;
using Sweep.Core.Rules;
using Sweep.Core.Safety;
using Sweep.Core.Storage;

namespace Sweep.App.ViewModels;

/// <summary>
/// 设置页：常规 / 白名单 / 可选 AI / 计划任务四个页签的全部状态与行为。
/// 密钥通过 IPasswordSupplier 读取（不进绑定系统），窗口关闭通过 IWindowController 请求。
/// </summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly IAppContext _app;
    private readonly IDialogService _dialogs;
    private readonly IAiCatalog _aiCatalog;
    private readonly IScheduleService _schedule;
    private readonly IWindowController _window;
    private readonly IPasswordSupplier _password;
    private readonly IThemeService _theme;
    private readonly AppSettings _settings;
    private readonly SecretStore _secrets;
    /// <summary>打开设置页时的计划任务开关状态：只有「开启 → 关闭」才需要真的调用 schtasks 移除。</summary>
    private bool _scheduleWasEnabled;

    public IReadOnlyList<ProviderDescriptor> Providers { get; }

    [ObservableProperty] private bool telemetryEnabled;
    [ObservableProperty] private bool trayEnabled;
    [ObservableProperty] private int themeIndex;
    [ObservableProperty] private string largeThresholdMb = "";
    [ObservableProperty] private string rulePackText = "";
    [ObservableProperty] private string whitelistText = "";
    [ObservableProperty] private string extensionsText = "";

    [ObservableProperty] private bool aiEnabled;
    [ObservableProperty] private bool consentAccepted;
    [ObservableProperty] private bool privacyMode;
    [ObservableProperty] private string budgetText = "";
    [ObservableProperty] private string aiStatusText = "";
    [ObservableProperty] private ProviderDescriptor? selectedProvider;
    [ObservableProperty] private string baseUrl = "";
    [ObservableProperty] private string model = "";
    [ObservableProperty] private string apiKeyMask = "";
    [ObservableProperty] private string usageText = "";

    [ObservableProperty] private bool scheduleEnabled;
    [ObservableProperty] private string trigger = "";
    [ObservableProperty] private string minFreePercentText = "";
    [ObservableProperty] private string scheduleDescription = "";

    [ObservableProperty] private string naturalText = "清掉三个月前的安装包，但别动 D 盘的";
    [ObservableProperty] private string dslText = "";
    [ObservableProperty] private string validationText = "";
    [ObservableProperty] private bool validationOk;
    [ObservableProperty] private int selectedTabIndex;

    public SettingsViewModel(
        IAppContext app,
        IDialogService dialogs,
        IAiCatalog aiCatalog,
        IScheduleService schedule,
        IWindowController window,
        IPasswordSupplier password,
        IThemeService theme)
    {
        _app = app;
        _dialogs = dialogs;
        _aiCatalog = aiCatalog;
        _schedule = schedule;
        _window = window;
        _password = password;
        _theme = theme;
        _settings = app.Settings;
        _secrets = app.Secrets;
        Providers = _aiCatalog.GetProviders();
        LoadUi();
    }

    partial void OnSelectedProviderChanged(ProviderDescriptor? value)
    {
        if (value is not null) ShowProvider(value);
    }

    /// <summary>选择即预览：所有已打开窗口通过 DynamicResource 即时换肤；取消时回滚。</summary>
    partial void OnThemeIndexChanged(int value) => _theme.Apply(ThemeModes.FromIndex(value));

    private void LoadUi()
    {
        TelemetryEnabled = _settings.TelemetryEnabled;
        TrayEnabled = _settings.TrayEnabled;
        ThemeIndex = ThemeModes.ToIndex(_settings.Theme);
        LargeThresholdMb = (_settings.LargeFileThresholdBytes / (1024 * 1024))
            .ToString(CultureInfo.InvariantCulture);
        RulePackText = $"内置规则包：{SettingsStore.BuiltinRulesPath} · 完整性校验：" +
                       (_app.Rules.IntegrityVerified ? "通过（.sha256）" : "本地分发包，未启用远程热更新") +
                       $" · 规则数 {_app.Rules.Rules.Count}";

        WhitelistText = string.Join(Environment.NewLine, _settings.Whitelist);
        ExtensionsText = string.Join(", ", _settings.ExcludeExtensions);

        AiEnabled = _settings.Ai.Enabled;
        ConsentAccepted = !string.IsNullOrEmpty(_settings.Ai.ConsentVersion);
        PrivacyMode = _settings.Ai.PrivacyMode;
        BudgetText = _settings.Ai.MonthlyBudgetUsd.ToString("0.##", CultureInfo.InvariantCulture);
        AiStatusText = _aiCatalog.Status;
        if (Providers.Count > 0)
            SelectedProvider = Providers.FirstOrDefault(p => p.Id == _settings.Ai.ProviderId) ?? Providers[0];
        else AiStatusText += " · 插件 DLL 缺失时设置页仍可打开，AI 面板不会出现";

        var (cost, requests) = _app.AiStorage.GetMonthUsage();
        UsageText = $"本月已用 ${cost:F4} / {requests} 次请求；上限 ${_settings.Ai.MonthlyBudgetUsd:0.##} / " +
                    $"{_settings.Ai.MaxRequestsPerMonth} 次。";

        ScheduleEnabled = _settings.Schedule.Enabled;
        _scheduleWasEnabled = _settings.Schedule.Enabled;
        Trigger = _settings.Schedule.Trigger;
        MinFreePercentText = _settings.Schedule.MinFreePercent.ToString(CultureInfo.InvariantCulture);
        ScheduleDescription = _schedule.Describe(_settings);
    }

    private void ShowProvider(ProviderDescriptor descriptor)
    {
        BaseUrl = descriptor.BaseUrl;
        Model = descriptor.DefaultModel;
        ApiKeyMask = "当前密钥：" + SecretStore.Mask(_secrets.GetApiKey(descriptor.Id));
    }

    [RelayCommand]
    private void ValidateWhitelist()
    {
        var patterns = SplitLines(WhitelistText);
        var result = Whitelist.Validate(patterns);
        ValidationOk = result.Ok;
        ValidationText = result.Ok ? $"✓ {patterns.Count} 条模式语法有效" : "✗ " + result.Error;
    }

    [RelayCommand]
    private void Translate() => DslText = _aiCatalog.TranslateRule(NaturalText);

    [RelayCommand]
    private void WriteRule()
    {
        var text = NaturalText;
        var exts = Regex.Matches(text, @"\.[a-zA-Z0-9]{2,5}", RegexOptions.None, TimeSpan.FromSeconds(1))
            .Select(m => m.Value.ToLowerInvariant()).Distinct().ToList();
        if (exts.Count == 0) exts = new List<string> { ".exe", ".msi", ".iso" };
        var numeric = Regex.Match(text, @"(\d+)\s*天", RegexOptions.None, TimeSpan.FromSeconds(1));
        int days = numeric.Success && int.TryParse(numeric.Groups[1].Value, out var d) ? d
                 : text.Contains("三个月") ? 90 : text.Contains("一个月") ? 30 : 90;

        var rules = RulePackLoader.LoadUserOverrides(SettingsStore.UserRulesPath).ToList();
        rules.RemoveAll(r => r.Id.StartsWith("user-nl-", StringComparison.Ordinal));
        rules.Add(new RuleDefinition
        {
            Id = "user-nl-" + Guid.NewGuid().ToString("N")[..8],
            Category = "自定义规则",
            Paths = exts.Select(x => $"%USERPROFILE%\\**\\*{x}").ToList(),
            MinAgeDays = Math.Clamp(days, 1, 3650),
            Risk = "medium",
            DefaultSelected = false,
            Handling = "glob-delete",
            Description = "由自然语言生成：" + text,
            ReclaimNote = "规则已写入用户规则包，下次扫描生效；默认不勾选。"
        });
        try
        {
            RulePackLoader.SaveUserOverrides(SettingsStore.UserRulesPath, rules);
            DslText = $"已写入 {rules.Count} 条用户规则（扩展名 {string.Join("、", exts)}，早于 {days} 天）。" +
                      "仅生成候选预览，下次扫描生效，绝不立即删除任何文件。";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DslText = "写入用户规则包失败：" + ex.Message;
        }
    }

    [RelayCommand]
    private void RegisterSchedule() =>
        ScheduleDescription = _schedule.Register(_settings, Environment.ProcessPath ?? "");

    [RelayCommand]
    private void UnregisterSchedule() => ScheduleDescription = _schedule.Unregister();

    [RelayCommand]
    private void Save()
    {
        var patterns = SplitLines(WhitelistText);
        var validation = Whitelist.Validate(patterns);
        ValidationOk = validation.Ok;
        if (!validation.Ok)
        {
            SelectedTabIndex = 1;
            ValidationText = "✗ " + validation.Error;
            _dialogs.Notify("白名单包含非法模式，已拒绝保存：" + validation.Error, warning: true);
            return;
        }

        _settings.Whitelist = patterns;
        _settings.ExcludeExtensions = ExtensionsText
            .Split(new[] { ',', '，', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
        _settings.TelemetryEnabled = TelemetryEnabled;
        _settings.TrayEnabled = TrayEnabled;
        _settings.Theme = ThemeModes.FromIndex(ThemeIndex);
        if (long.TryParse(LargeThresholdMb, out long mb) && mb > 0)
            _settings.LargeFileThresholdBytes = Math.Clamp(mb, 1, 100_000) * 1024 * 1024;

        _settings.Ai.Enabled = AiEnabled;
        _settings.Ai.ConsentVersion = ConsentAccepted ? "v1" : null;
        _settings.Ai.PrivacyMode = PrivacyMode;
        _settings.Ai.ProviderId = SelectedProvider?.Id;
        if (double.TryParse(BudgetText, NumberStyles.Float, CultureInfo.InvariantCulture, out double budget))
            _settings.Ai.MonthlyBudgetUsd = Math.Clamp(budget, 0, 1000);

        _settings.Schedule.Enabled = ScheduleEnabled;
        _settings.Schedule.Trigger = string.IsNullOrWhiteSpace(Trigger)
            ? "weekly:Mon@03:00" : Trigger.Trim();
        if (int.TryParse(MinFreePercentText, out int percent))
            _settings.Schedule.MinFreePercent = Math.Clamp(percent, 1, 99);

        try
        {
            if (_app.Loaded.ReadOnly)
                throw new InvalidOperationException("设置文件由更新版本创建，已拒绝写入，请升级 Sweep。");
            SettingsStore.Save(_settings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            _dialogs.Notify("设置保存失败：" + ex.Message, warning: true);
            return;
        }

        if (SelectedProvider is { } provider)
        {
            var key = _password.ReadPassword();
            if (key.Length > 0) _secrets.SetApiKey(provider.Id, key);
            WriteProviderOverrides(provider);
        }

        // 只在必要时碰任务计划程序：默认设置里计划任务是关闭的，若每次保存都无条件调用
        // schtasks /Delete，会在默认配置下也起一个外部进程（最长阻塞 15 秒、受管控环境还会失败）。
        // 开启时 Register 顺带更新触发器；关闭时仅在本次确实从「开启」切到「关闭」才移除。
        string scheduleResult;
        if (_settings.Schedule.Enabled)
            scheduleResult = _schedule.Register(_settings, Environment.ProcessPath ?? "");
        else if (_scheduleWasEnabled)
            scheduleResult = _schedule.Unregister();
        else
            scheduleResult = "计划任务未启用，未调用任务计划程序";
        ;

        // 运行中关闭开关：立即取消 in-flight 请求，无需重启
        if (!_settings.Ai.Enabled) _app.DisableAiImmediately();

        _window.Close(true);
    }

    private void WriteProviderOverrides(ProviderDescriptor provider)
    {
        var url = BaseUrl.Trim();
        var model = Model.Trim();
        if (url.Length == 0 || (url == provider.BaseUrl && model == provider.DefaultModel)) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_app.ProviderOverridesPath)!);
            var payload = new
            {
                userOverrides = new[]
                {
                    new
                    {
                        id = provider.Id,
                        label = provider.Label,
                        kind = provider.Kind,
                        baseUrl = url,
                        defaultModel = string.IsNullOrEmpty(model) ? provider.DefaultModel : model,
                        jsonMode = provider.JsonMode,
                        builtin = provider.Builtin
                    }
                },
                disabled = Array.Empty<string>()
            };
            File.WriteAllText(_app.ProviderOverridesPath,
                JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ;
        }
    }

    private static List<string> SplitLines(string text) => text
        .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
        .Select(s => s.Trim()).Where(s => s.Length > 0).ToList();

    [RelayCommand]
    private void Cancel()
    {
        _theme.Apply(_settings.Theme);   // 未保存的主题预览回滚到持久化偏好
        _window.Close(false);
    }
}
