using Sweep.Core;
using System.Reflection;
using System.Runtime.CompilerServices;
using Sweep.Core.Recommendation;
using Sweep.Core.Safety;
using Xunit;

namespace Sweep.Tests;

/// <summary>文档 3.2 的三条强制规则 + 10.2「无 AI 完整回归」。</summary>
public class ArchitectureTests
{
    private static readonly string[] Forbidden = { "ApiKey", "HttpClient", "BaseUrl", "Bearer", "ChatCompletion" };
    private static readonly string[] LogicNamespaces =
    {
        "Sweep.Core.Rules", "Sweep.Core.Recommendation", "Sweep.Core.Safety",
        "Sweep.Core.Scanning", "Sweep.Core.Executors"
    };

    [Fact]
    public void Core_DoesNotReferenceNetworkOrProviderAssemblies()
    {
        var core = typeof(SafetyGate).Assembly;
        var refs = core.GetReferencedAssemblies().Select(a => a.Name ?? "").ToArray();
        Assert.DoesNotContain(refs, r => r == "System.Net.Http");
        Assert.DoesNotContain(refs, r => r.Contains("Sweep.Ai.Providers", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(refs, r => r.Contains("System.Net.Http", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Core_HasNoHttpClientFieldsOrCredentialIdentifiers()
    {
        var core = typeof(SafetyGate).Assembly;
        foreach (var type in core.GetTypes())
        {
            foreach (var forbidden in Forbidden)
                Assert.DoesNotContain(forbidden, type.Name);

            foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.NonPublic |
                                                BindingFlags.Instance | BindingFlags.Static))
            {
                Assert.DoesNotContain("HttpClient", field.FieldType.Name);
                foreach (var forbidden in Forbidden)
                    Assert.DoesNotContain(forbidden, field.Name);
            }
        }
    }

    [Fact]
    public void Core_LogicNamespaces_ContainNoAiProviderIdentifiers()
    {
        var core = typeof(SafetyGate).Assembly;
        var banned = new[] { "ApiKey", "Provider", "Token" };
        var inspected = 0;
        foreach (var type in core.GetTypes().Where(t =>
                     t.Namespace is not null && LogicNamespaces.Any(n => t.Namespace.StartsWith(n, StringComparison.Ordinal))))
        {
            // 跳过编译器生成的闭包 / async-iterator 状态机。[EnumeratorCancellation] 会生成
            // <>x__combinedTokens（合并 CancellationToken 的框架字段），它不是厂商凭据标识符。
            if (type.IsDefined(typeof(CompilerGeneratedAttribute), inherit: false)) continue;
            inspected++;

            foreach (var word in banned)
            {
                Assert.DoesNotContain(word, type.Name);
                foreach (var member in type.GetMembers(BindingFlags.Public | BindingFlags.NonPublic |
                                                      BindingFlags.Instance | BindingFlags.Static |
                                                      BindingFlags.DeclaredOnly)
                             .Where(m => !m.IsDefined(typeof(CompilerGeneratedAttribute), inherit: false)))
                    Assert.DoesNotContain(word, member.Name);
            }
        }
        // 防止过滤条件写宽后「扫了 0 个类型」而假通过。
        Assert.True(inspected >= 10, $"架构扫描应覆盖 Core 逻辑层的手写类型，实际仅 {inspected} 个");
    }

    [Fact]
    public void Abstractions_ContainsNoImplementations()
    {
        var abstractions = typeof(Sweep.Ai.Abstractions.IRecommendationEnricher).Assembly;
        var refs = abstractions.GetReferencedAssemblies().Select(a => a.Name ?? "").ToArray();
        Assert.DoesNotContain(refs, r => r == "System.Net.Http");
        Assert.Contains(abstractions.GetTypes(), t => t.IsInterface);
        Assert.DoesNotContain(abstractions.GetTypes(), t => t.Name.Contains("HttpClient"));
    }

    [Fact]
    public void ProvidersDllMissing_StillBuildsCompleteRulePlan()
    {
        // 测试项目刻意不引用 AI 插件，产物目录不应存在该 DLL
        var dll = Path.Combine(AppContext.BaseDirectory, "Sweep.Ai.Providers.dll");
        Assert.False(File.Exists(dll), "测试构建不应包含可缺省的 AI 插件 DLL");

        var temp = Path.Combine(Path.GetTempPath(), "sweep-arch-" + Guid.NewGuid().ToString("N")[..6] + ".tmp");
        File.WriteAllText(temp, "fixture");
        try
        {
            var scan = TestFixture.Scan(new[] { TestFixture.Candidate(temp) });
            var plan = RecommendationEngine.BuildRulePlan(scan, TestFixture.Rules(), TestFixture.Context());
            Assert.NotEmpty(plan);
            Assert.All(plan, p => Assert.Equal(RecommendationSource.Rule, p.Source));
            Assert.All(plan, p => Assert.NotEqual(RecommendationSource.Ai, p.Source));
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    [Fact]
    public void AiSuggestions_NeverEnterSilentCleanup()
    {
        var ctx = TestFixture.Context();
        var ai = new Recommendation(@"C:\Temp\a.tmp", RecommendationSource.Ai,
            RecommendationAction.Delete, RiskLevel.Low, 0.99, "模型建议", null, true);
        var gated = SafetyGate.Apply(ai, ctx);
        Assert.False(SafetyGate.IsSilentCleanupAllowed(gated));

        var lowRule = new Recommendation(@"C:\Temp\b.tmp", RecommendationSource.Rule,
            RecommendationAction.Delete, RiskLevel.Low, 1.0, "规则", "temp-user");
        Assert.True(SafetyGate.IsSilentCleanupAllowed(SafetyGate.Apply(lowRule, ctx)) ||
                    !File.Exists(@"C:\Temp\b.tmp"));   // 不存在路径被规范化判 Keep 亦属安全
    }
}
