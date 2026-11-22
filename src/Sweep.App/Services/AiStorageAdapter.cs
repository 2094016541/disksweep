using System.IO;
using Sweep.Ai.Abstractions;
using Sweep.Core.Storage;

namespace Sweep.App.Services;

/// <summary>把 Providers 需要的持久化能力适配到 Core 的 SQLite / DPAPI；Providers 不引用 Core。</summary>
public sealed class AiStorageAdapter : IAiStorage
{
    private readonly SweepDb _db;
    private readonly SecretStore _secrets;

    public AiStorageAdapter(SweepDb db, SecretStore secrets)
    {
        _db = db;
        _secrets = secrets;
    }

    public string? GetCachedResult(string fingerprint, TimeSpan maxAge) => _db.GetAiCache(fingerprint, maxAge);

    public void PutCachedResult(string fingerprint, string provider, string model,
        string promptVersion, string resultJson, DateTime createdUtc) =>
        _db.PutAiCache(fingerprint, provider, model, promptVersion, resultJson, createdUtc);

    public double GetMonthUsageUsd(DateTime utcNow) => _db.GetMonthUsage(utcNow).Cost;
    public int GetMonthRequestCount(DateTime utcNow) => _db.GetMonthUsage(utcNow).Requests;

    public void AddUsage(DateTime utcNow, string provider, int requests,
        int inputTokens, int outputTokens, double estCostUsd) =>
        _db.AddAiUsage(utcNow, provider, requests, inputTokens, outputTokens, estCostUsd);

    public void PruneCache(int maxEntries, TimeSpan maxAge) => _db.PruneAiCache(maxEntries, maxAge);

    public string? GetSecret(string providerId) => _secrets.GetApiKey(providerId);
    public void SetSecret(string providerId, string? value) => _secrets.SetApiKey(providerId, value);
    public string GetOrCreateSalt() => _secrets.GetOrCreateSalt();

    public (double Cost, int Requests) GetMonthUsage() => _db.GetMonthUsage(DateTime.UtcNow);
}
