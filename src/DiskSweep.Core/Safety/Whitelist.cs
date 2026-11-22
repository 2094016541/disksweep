using DiskSweep.Core.Rules;

namespace DiskSweep.Core.Safety;

/// <summary>用户白名单 / 排除规则；命中即否决（文档 7.1）。</summary>
public sealed class Whitelist
{
    private readonly IReadOnlyList<GlobMatcher> _matchers;
    private readonly IReadOnlyList<string> _extensions;

    public IReadOnlyList<string> Patterns { get; }

    public Whitelist(IEnumerable<string> patterns, IEnumerable<string>? extensions = null)
    {
        Patterns = patterns.ToArray();
        var compiled = new List<GlobMatcher>();
        foreach (var p in Patterns)
        {
            var normalized = PathNormalizer.NormalizePattern(p);
            if (GlobMatcher.TryCreate(normalized, out var m, out _) && m is not null)
                compiled.Add(m);
        }
        _matchers = compiled;
        _extensions = (extensions ?? Array.Empty<string>()).ToArray();
    }

    public bool Matches(string normalizedPath)
    {
        if (_matchers.Any(m => m.Matches(normalizedPath))) return true;
        var ext = Path.GetExtension(normalizedPath);
        return ext.Length > 0 && _extensions.Any(e => string.Equals(e, ext, StringComparison.OrdinalIgnoreCase));
    }

    public static (bool Ok, string? Error) Validate(IEnumerable<string> patterns)
    {
        foreach (var p in patterns)
        {
            var normalized = PathNormalizer.NormalizePattern(p);
            if (!GlobMatcher.TryCreate(normalized, out _, out var error))
                return (false, $"非法模式 \"{p}\"：{error}");
        }
        return (true, null);
    }
}
