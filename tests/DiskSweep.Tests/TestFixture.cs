using DiskSweep.Core;
using DiskSweep.Core.Recommendation;
using DiskSweep.Core.Rules;
using DiskSweep.Core.Safety;
using DiskSweep.Core.Scanning;

namespace DiskSweep.Tests;

public static class TestFixture
{
    public static string RulesPath => Path.Combine(AppContext.BaseDirectory, "rules", "builtin-win.json");

    public static RuleEngine Rules(string? overrides = null) => RuleEngine.Create(RulesPath, overrides);

    public static SafetyContext Context(params string[] whitelist) =>
        SafetyContext.Create(new Whitelist(whitelist));

    public static PlanItem Plan(
        string path,
        long size = 4096,
        RecommendationSource source = RecommendationSource.Rule,
        RecommendationAction action = RecommendationAction.Delete,
        RiskLevel risk = RiskLevel.Low,
        string? ruleId = "temp-user",
        bool explicitConfirm = false,
        ulong volume = 0,
        ulong fileId = 0,
        bool isSystem = false,
        string category = "临时文件")
    {
        var rec = new Recommendation(path, source, action, risk, 1.0, "测试依据", ruleId, explicitConfirm);
        return new PlanItem
        {
            Path = path,
            Recommendation = rec,
            Category = category,
            AllocationSize = size,
            VolumeSerial = volume,
            FileId = fileId,
            IsDirectory = false,
            Handling = "glob-delete",
            RequiresAdmin = false,
            Description = "测试项",
            ReclaimNote = "",
            IsSystemOperation = isSystem,
            LowReclaimEstimate = false,
            AgeDays = 30,
            DefaultSelected = true,
            IsSelected = true
        };
    }

    public static ScanResult Scan(IEnumerable<CandidateItem> items, RuleEngine? rules = null)
    {
        var list = items.ToList();
        var engine = rules ?? Rules();
        var matches = new Dictionary<string, RuleMatch>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in list)
        {
            var match = engine.Match(item.Path.Replace('/', '\\'), item.AgeDays);
            if (match is not null) matches[item.Path] = match;
        }
        var snapshot = new ScanSnapshot("test-" + Guid.NewGuid().ToString("N"), "C:\\",
            DateTime.UtcNow, DateTime.UtcNow, ScanMode.Walk, list.Count,
            list.Sum(i => i.AllocationSize), 0);
        return new ScanResult(snapshot, list, matches, Array.Empty<CategorySummary>(),
            Array.Empty<DuplicateGroup>(), new ScanStats(), false, null, 0);
    }

    public static CandidateItem Candidate(string path, long size = 4096, int ageDays = 30) =>
        new(path, "其他", size, size, ageDays, Path.GetExtension(path).ToLowerInvariant(),
            FileAttributes.Normal, 0, 0, false);

    public static string NewTempDirectory()
    {
        var dir = Path.Combine(Path.GetTempPath(), "disksweep-test-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }
}
