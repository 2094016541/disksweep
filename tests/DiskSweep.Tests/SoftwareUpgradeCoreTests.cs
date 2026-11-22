using DiskSweep.Core.Software;
using Xunit;

namespace DiskSweep.Tests;

/// <summary>
/// 版本升级的纯逻辑：版本比较、winget 输出解析、包关联与命令行构造。
/// 全部使用真实采集到的 winget 输出片段，不启动任何进程。
/// </summary>
public class SoftwareUpgradeCoreTests
{
    /// <summary>
    /// 下面是 winget 1.29.380 的真实输出（原样粘贴，行宽由 winget 自己决定）。
    /// 注意中文表头「名称 / 版本 / 可用」各占两个显示列——手写对齐很容易差一列，
    /// 所以测试桩必须用真实文本，否则测的是「我以为的对齐」而不是 winget 的对齐。
    /// </summary>
    private static readonly string ChineseUpgradeTable = string.Join("\n",
        "名称                                                         ID                                     版本          可用          源",
        "--------------------------------------------------------------------------------------------------------------------------------------",
        "Another Redis Desktop Manager 1.7.1                          qishibo.AnotherRedisDesktopManager     1.7.1         1.7.4         winget",
        "Apifox 2.8.45                                                Ruihu.Apifox                           2.8.45        2.8.55        winget",
        "DBX                                                          t8y2.DBX                               < 0.6.45      0.6.72        winget",
        "Git                                                          Git.Git                                2.55.0.2      2.56.1        winget",
        "搜索源时失败;结果将不包括在内： msstore",
        "");

    private static readonly string EnglishUpgradeTable = string.Join("\n",
        "Name                 ID                Version   Available   Source",
        "------------------------------------------------------------------",
        "7-Zip                7zip.7zip         24.09     26.06       winget",
        "");

    private static readonly string VersionList = string.Join("\n",
        "已找到 7-Zip [7zip.7zip]",
        "版本",
        "-----",
        "26.06",
        "26.05",
        "26.05",
        "24.09",
        "19.00",
        "8.0 Beta 10",
        "Windows 程序包管理器 v1.29.380",
        "");

    private static SoftwareEntry Entry(string name, string version = "1.0") =>
        new("Machine64:" + name, name, version, "Acme", @"C:\Program Files\Acme", "",
            "MsiExec.exe /X{TEST}", 0, SoftwareScope.Machine64, false);

    [Theory]
    [InlineData("1.10", "1.9")]
    [InlineData("1.2.3", "1.2")]
    [InlineData("2026.3", "2025.3.1")]
    [InlineData("2.0", "2.0 rc1")]
    [InlineData("26.06", "26.05")]
    public void SoftwareVersion_OrdersNumericAndPrereleaseVersions(string newer, string older)
    {
        Assert.True(SoftwareVersion.IsNewer(newer, older));
        Assert.False(SoftwareVersion.IsNewer(older, newer));
    }

    [Fact]
    public void SoftwareVersion_HandlesEqualCurrentVersionAndUnknownCurrent()
    {
        Assert.Equal("当前版本", SoftwareVersion.DescribeRelation("1.2.0", "1.2"));
        Assert.Equal("更新版本", SoftwareVersion.DescribeRelation("1.3", "1.2"));
        Assert.Equal("旧版本", SoftwareVersion.DescribeRelation("1.1", "1.2"));
        Assert.Equal("可安装版本", SoftwareVersion.DescribeRelation("1.3", "—"));
    }

    [Fact]
    public void SoftwareVersion_SortDescendingDeduplicatesAndKeepsSpacedVersions()
    {
        var sorted = SoftwareVersion.SortDescending(new[] { "1.2", "26.05", "1.10", "26.05", "8.0 Beta 10" });

        Assert.Equal(new[] { "26.05", "8.0 Beta 10", "1.10", "1.2" }, sorted);
    }

    [Fact]
    public void ParseUpgradable_ReadsChineseTableAndSkipsHeadersAndNoise()
    {
        var packages = WingetOutputParser.ParseUpgradable(ChineseUpgradeTable);

        // 表头、分隔线、以及末尾的「搜索源时失败…」提示行都不是数据行；
        // 其余每一行（含名称带版本号的 Another Redis Desktop Manager、版本列带 < 前缀的 DBX）
        // 都是合法的可升级包，必须全部解析出来。
        Assert.Equal(4, packages.Count);

        Assert.Equal("qishibo.AnotherRedisDesktopManager", packages[0].Id);
        Assert.Equal("1.7.4", packages[0].AvailableVersion);
        Assert.Equal("1.7.1", packages[0].CurrentVersion);

        Assert.Equal("Ruihu.Apifox", packages[1].Id);
        Assert.Equal("2.8.55", packages[1].AvailableVersion);
        Assert.Equal("2.8.45", packages[1].CurrentVersion);

        Assert.Equal("t8y2.DBX", packages[2].Id);
        Assert.Equal("0.6.72", packages[2].AvailableVersion);

        Assert.Equal("Git.Git", packages[3].Id);
        Assert.Equal("2.56.1", packages[3].AvailableVersion);
        Assert.Equal("winget", packages[3].Source);
    }

    [Fact]
    public void ParseUpgradable_ReadsEnglishTable()
    {
        var packages = WingetOutputParser.ParseUpgradable(EnglishUpgradeTable);

        Assert.Single(packages);
        Assert.Equal("7zip.7zip", packages[0].Id);
        Assert.Equal("26.06", packages[0].AvailableVersion);
    }

    [Fact]
    public void ParseUpgradable_IsSafeOnNullOrEmptyOutput()
    {
        Assert.Empty(WingetOutputParser.ParseUpgradable(null));
        Assert.Empty(WingetOutputParser.ParseUpgradable(""));
        Assert.Empty(WingetOutputParser.ParseVersionList(null));
    }

    private static readonly string SearchTable = string.Join("\n",
        "名称               ID                 版本      匹配             源",
        "----------------------------------------------------------------------------",
        "Apifox             Ruihu.Apifox       2.8.55                     winget",
        "Apifox Helper      Apifox.Helper                             winget",
        "7-Zip 24.09 (x64)  7zip.7zip          24.09     7zip           winget",
        "");

    /// <summary>
    /// search 的「版本 / 匹配」两列可以整列为空，同一行可能只有 2~5 个字段。
    /// 按位置硬取第 3 列会把匹配值（7zip）当成版本号，所以只在剩余列里挑真正的版本。
    /// </summary>
    [Fact]
    public void ParseSearch_ToleratesMissingVersionAndMatchColumns()
    {
        var packages = WingetOutputParser.ParseSearch(SearchTable);

        Assert.Equal(3, packages.Count);
        Assert.Equal(new[] { "Ruihu.Apifox", "Apifox.Helper", "7zip.7zip" },
                     packages.Select(p => p.Id).ToArray());
        Assert.Equal("2.8.55", packages[0].CurrentVersion);
        Assert.Equal("winget", packages[0].Source);
        Assert.Equal("", packages[1].CurrentVersion);          // 版本列整列为空
        Assert.Equal("24.09", packages[2].CurrentVersion);     // 不能把「匹配」列的 7zip 当版本
        Assert.Equal("", packages[2].AvailableVersion);        // search 不报告可升级版本
        Assert.Empty(WingetOutputParser.ParseSearch(null));
        Assert.Empty(WingetOutputParser.ParseSearch(""));
    }

    [Fact]
    public void WingetPackage_DisplayShowsNameAndIdTogether()
    {
        var package = new WingetPackage("Apifox 2.8.45", "Ruihu.Apifox", "2.8.45", "2.8.55", "winget");

        Assert.Equal("Apifox 2.8.45 · Ruihu.Apifox", package.Display);
        Assert.Equal(package.Display, package.ToString());       // 自绘模板退化成 ToString() 时仍可读
        Assert.Equal("没有候选", new WingetPackage("没有候选", "", "", "", "").Display);
    }

    [Fact]
    public void ParseVersionList_FiltersHeadersAndOrdersNewestFirst()
    {
        var versions = WingetOutputParser.ParseVersionList(VersionList);

        Assert.Equal(new[] { "26.06", "26.05", "24.09", "19.00", "8.0 Beta 10" }, versions);
    }

    [Fact]
    public void UpgradeCatalog_MatchesRegistryEntryToWingetPackage()
    {
        var packages = WingetOutputParser.ParseUpgradable(ChineseUpgradeTable);

        Assert.Equal("Ruihu.Apifox", UpgradeCatalog.Resolve(Entry("Apifox 2.8.45", "2.8.45"), packages)?.Id);
        Assert.Equal("Ruihu.Apifox", UpgradeCatalog.Resolve(Entry("Apifox", "2.8.45"), packages)?.Id);
        Assert.Null(UpgradeCatalog.Resolve(Entry("GitHub CLI", "1.0"), packages));
    }

    private static readonly string ChineseSourceList = string.Join("\n",
        "名称        参数                                          显式",
        "---------------------------------------------------------------",
        "msstore     https://storeedgefd.dsx.mp.microsoft.com/v9.0 false",
        "winget      https://cdn.winget.microsoft.com/cache        false",
        "winget-font https://cdn.winget.microsoft.com/fonts        true",
        "");
    private static readonly string EnglishSourceList = string.Join("\n",
        "Name        Argument                                       Explicit",
        "---------------------------------------------------------------",
        "winget      https://cdn.winget.microsoft.com/cache        false",
        "");

    [Fact]
    public void ParseSourceList_ReadsChineseAndEnglishTablesAndSkipsHeaders()
    {
        var chinese = WingetSources.ParseList(ChineseSourceList);
        Assert.Equal(3, chinese.Count);
        Assert.Equal("msstore", chinese[0].Name);
        Assert.Equal("https://storeedgefd.dsx.mp.microsoft.com/v9.0", chinese[0].Url);
        Assert.False(chinese[0].IsExplicit);
        Assert.Equal("winget-font", chinese[2].Name);
        Assert.True(chinese[2].IsExplicit);

        var english = WingetSources.ParseList(EnglishSourceList);
        Assert.Single(english);
        Assert.Equal("winget", english[0].Name);
        Assert.Empty(WingetSources.ParseList(null));
    }

    [Fact]
    public void ParseSourceList_RejectsNamesThatCouldBreakCommandLine()
    {
        const string malicious = "名称        参数                  显式\n" +
                                 "evil&calc   https://example.com/a  false\n" +
                                 "winget      https://cdn.example.com false";
        var sources = WingetSources.ParseList(malicious);
        Assert.Single(sources);
        Assert.Equal("winget", sources[0].Name);
        Assert.False(WingetSources.IsValidName("winget & calc"));
        Assert.False(WingetSources.IsValidName("--force"));
    }

    [Fact]
    public void MergeVersions_KeepsEachSourceAsASelectableOption()
    {
        var merged = WingetSources.MergeVersions(new[]
        {
            new KeyValuePair<string, IReadOnlyList<string>>("winget", new[] { "26.06", "26.05", "26.06" }),
            new KeyValuePair<string, IReadOnlyList<string>>("msstore", new[] { "26.06", "25.10" })
        });

        Assert.Equal(4, merged.Count);                       // 跨源同版本不去重
        Assert.Equal("26.06", merged[0].Version);
        Assert.Equal("msstore", merged[0].Source);           // 同版本按源名排序
        Assert.Equal("winget", merged[1].Source);
        Assert.Contains(merged, v => v.Version == "26.05" && v.Source == "winget");
        Assert.Contains(merged, v => v.Version == "25.10" && v.Source == "msstore");
        Assert.DoesNotContain(merged, v => v.Version == "26.06" && v.Source == "bad&source");
    }

    [Fact]
    public void UpgradeRequest_CarriesSourceAsOneValidatedArgument()
    {
        var request = UpgradeRequest.TryCreate("7zip.7zip", null, "winget-font", out string error);

        Assert.NotNull(request);
        Assert.Equal("", error);
        Assert.Equal(new[] { "upgrade", "--id", "7zip.7zip", "--exact", "--source",
                             "winget-font", "--interactive" }, request!.BuildArguments());
        Assert.Contains("winget-font", request.Describe());
    }

    [Theory]
    [InlineData("winget & calc")]
    [InlineData("--force")]
    [InlineData("a/b")]
    [InlineData("源\"B")]
    public void UpgradeRequest_RejectsInvalidSource(string source)
    {
        Assert.Null(UpgradeRequest.TryCreate("7zip.7zip", "24.09", source, out string error));
        Assert.NotEmpty(error);
    }

    [Fact]
    public void UpgradeRequest_RefusesInvalidSourceEvenWhenConstructedDirectly()
    {
        Assert.Throws<InvalidOperationException>(() =>
            new UpgradeRequest("7zip.7zip", null, "bad source").BuildArguments());
    }

    [Fact]
    public void UpgradeRequest_DefaultsToLatestWithoutVersionArgument()
    {
        var request = UpgradeRequest.TryCreate("Git.Git", null, out string error);

        Assert.NotNull(request);
        Assert.Equal("", error);
        Assert.True(request!.IsLatest);
        var arguments = request.BuildArguments();
        Assert.Equal(new[] { "upgrade", "--id", "Git.Git", "--exact", "--interactive" }, arguments);
        Assert.DoesNotContain("--version", arguments);
        Assert.Equal("winget upgrade --id Git.Git --exact --interactive", request.Describe());
    }

    [Fact]
    public void UpgradeRequest_PassesSpecifiedVersionAsOneToken()
    {
        var request = UpgradeRequest.TryCreate("7zip.7zip", "8.0 Beta 10", out _);

        Assert.NotNull(request);
        Assert.False(request!.IsLatest);
        var arguments = request.BuildArguments();
        Assert.Contains("--version", arguments);
        Assert.Equal("8.0 Beta 10", arguments[arguments.Count - 2]);
        Assert.Contains("--interactive", arguments);
    }

    [Theory]
    [InlineData("Git.Git & calc", null)]
    [InlineData("ARP\\Machine\\X64\\App", null)]
    [InlineData("", null)]
    [InlineData("Git.Git", "1.0 | shutdown")]
    [InlineData("Git.Git", "\"2.0\"")]
    [InlineData("Git.Git", "2.0 && del *")]
    public void UpgradeRequest_RejectsInjectionAndUnsafeTokens(string id, string? version)
    {
        Assert.Null(UpgradeRequest.TryCreate(id, version, out string error));
        Assert.NotEmpty(error);
    }

    [Fact]
    public void UpgradeRequest_NeverBuildsSilentOrForcedCommands()
    {
        foreach (var (version, source) in new (string?, string?)[]
                 {
                     (null, null), ("24.09", "winget"), ("8.0 Beta 10", "winget-font")
                 })
        {
            var request = UpgradeRequest.TryCreate("7zip.7zip", version, source, out _);
            var arguments = request!.BuildArguments();
            foreach (var banned in new[] { "--silent", "-h", "--force", "--accept-package-agreements",
                                              "--accept-source-agreements", "--disable-interactivity" })
                Assert.DoesNotContain(banned, arguments);
        }
    }

    [Fact]
    public void UpgradeRequest_RefusesToBuildArgumentsForInvalidRequest()
    {
        Assert.Throws<InvalidOperationException>(() =>
            new UpgradeRequest("bad id", null).BuildArguments());
    }

    /// <summary>
    /// 源下拉的内容契约：只列 winget 里真正可用的源（所有源 → 国内镜像 → 官方源 → 用户自己的源）。
    /// 没添加过的预置源不进下拉——选它只会让 winget 报错，那不是「可选项」而是坑。
    /// </summary>
    [Fact]
    public void BuildChoices_ListsOnlyEnabledSourcesAndKeepsCustomSourcesLast()
    {
        var configured = new[]
        {
            new WingetSource("ustc", "https://mirrors.ustc.edu.cn/winget-source", true),
            new WingetSource("winget", "https://cdn.winget.microsoft.com/cache", false),
            new WingetSource("winget-font", "https://cdn.winget.microsoft.com/fonts", true)
        };

        var choices = WingetSources.BuildChoices(configured, WingetSourceCatalog.Presets);
        var names = choices.Select(c => c.Name).ToArray();

        Assert.True(choices[0].IsAll);
        Assert.Equal("", choices[0].Name);
        // 国内镜像排在官方源之前——这个顺序就是「默认推荐谁」的表达。
        Assert.True(Array.IndexOf(names, "ustc") < Array.IndexOf(names, "winget"));
        // 没添加的 cernet / msstore 不出现。
        Assert.DoesNotContain("cernet", names);
        Assert.DoesNotContain("msstore", names);
        Assert.All(choices.Skip(1), c => Assert.True(c.IsConfigured));

        var mirror = choices.Single(c => c.Name == "ustc");
        Assert.True(mirror.IsDomestic);
        Assert.Equal("中科大镜像 · 已启用", mirror.Display);
        Assert.Equal("https://mirrors.ustc.edu.cn/winget-source", mirror.Url);

        var official = choices.Single(c => c.Name == "winget");
        Assert.Equal("微软官方源 · 已启用", official.Display);

        // 不在内置目录里的源（winget-font 这类辅助源、用户自建的源）原样追加，不隐藏。
        Assert.Equal("winget-font", choices[^1].Name);
        Assert.True(choices[^1].IsCustom);
        Assert.Null(choices[^1].Preset);
    }

    /// <summary>源清单读不出来时只留「所有软件源」：不能把没确认的预置源伪装成可选项。</summary>
    [Fact]
    public void BuildChoices_WithUnknownOrEmptySourceList_OffersOnlyAllSources()
    {
        var unknown = WingetSources.BuildChoices(null, WingetSourceCatalog.Presets, statusKnown: false);
        Assert.Single(unknown);
        Assert.True(unknown[0].IsAll);

        var empty = WingetSources.BuildChoices(Array.Empty<WingetSource>(), WingetSourceCatalog.Presets);
        Assert.Single(empty);
        Assert.True(empty[0].IsAll);
    }

    [Fact]
    public void Recommend_OnlyReturnsMirrorsThatAreActuallyEnabled()
    {
        Assert.Null(WingetSourceCatalog.Recommend(new[] { "winget" }));          // 官方源不算国内
        Assert.Null(WingetSourceCatalog.Recommend(Array.Empty<string>()));
        Assert.Null(WingetSourceCatalog.Recommend(new[] { "bad name" }));        // 非法名被忽略
        Assert.Null(WingetSourceCatalog.Recommend(null));
        Assert.Equal("cernet", WingetSourceCatalog.Recommend(new[] { "winget", "cernet" }));
        Assert.Equal("ustc", WingetSourceCatalog.Recommend(new[] { "ustc", "cernet" }));
    }

    [Fact]
    public void FirstUnconfiguredDomestic_SuggestsTheMirrorThatIsNotAddedYet()
    {
        Assert.Equal("ustc", WingetSourceCatalog.FirstUnconfiguredDomestic(null)?.Name);
        Assert.Equal("cernet", WingetSourceCatalog.FirstUnconfiguredDomestic(new[] { "ustc" })?.Name);
        Assert.Null(WingetSourceCatalog.FirstUnconfiguredDomestic(new[] { "ustc", "cernet", "winget" }));
    }

    [Fact]
    public void AddArguments_AddsTrustLevelOnlyOnVersionsThatKnowIt()
    {
        var preset = WingetSourceCatalog.Find("ustc")!;

        var modern = WingetSourceCatalog.AddArguments(preset, "1.29.380");
        Assert.Contains("--trust-level", modern);
        Assert.Equal("trusted", modern[modern.ToList().IndexOf("--trust-level") + 1]);
        Assert.Equal("ustc", modern[modern.ToList().IndexOf("--name") + 1]);
        Assert.Equal("https://mirrors.ustc.edu.cn/winget-source", modern[modern.ToList().IndexOf("--arg") + 1]);

        Assert.DoesNotContain("--trust-level", WingetSourceCatalog.AddArguments(preset, "1.5.0"));
        Assert.Contains("--trust-level", WingetSourceCatalog.AddArguments(preset, null));   // 未知版本按新版

        // 协议必须由用户在控制台里自己确认，命令里不得带自动接受参数。
        Assert.DoesNotContain("--accept-source-agreements", modern);
        Assert.DoesNotContain("--accept-package-agreements", modern);
    }

    /// <summary>
    /// 内置源目录只放实测可用的镜像。网上流传的「阿里云 / 腾讯云 / 清华 winget 镜像」
    /// 实测分别是 404 / DNS 不通 / 404，不能写进预置列表去骗用户。
    /// </summary>
    [Fact]
    public void Presets_AreTheMirrorsThatActuallyExist()
    {
        Assert.Equal(new[] { "ustc", "cernet", "winget", "msstore" },
                     WingetSourceCatalog.Presets.Select(p => p.Name).ToArray());
        Assert.All(WingetSourceCatalog.Presets,
                   p => Assert.True(WingetSources.IsValidUrl(p.Url)));
        Assert.All(WingetSourceCatalog.Presets,
                   p => Assert.True(WingetSources.IsValidName(p.Name)));
        Assert.Equal(2, WingetSourceCatalog.Presets.Count(p => p.Kind == WingetSourceKind.Domestic));
    }
}
