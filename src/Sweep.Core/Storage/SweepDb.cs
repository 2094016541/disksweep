using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Sweep.Core.Recommendation;

namespace Sweep.Core.Storage;

public sealed class UnsupportedSchemaException : Exception
{
    public UnsupportedSchemaException(string message) : base(message) { }
}

/// <summary>文档 5.1 表结构 + 5.3 生命周期。所有 SQL 均参数绑定，无拼接。</summary>
public sealed class SweepDb
{
    private const int CurrentSchemaVersion = 3;
    private readonly string _connectionString;

    public string DatabasePath { get; }

    /// <summary>
    /// 安装版（exe 同目录存在 portable.mode）数据落在安装目录 data\，让缓存与程序同处用户所选目录；
    /// 绿色版 / 开发态仍保持每用户 LocalAppData 语义。
    /// </summary>
    public static string AppDataDirectory => ResolveAppDataDirectory(
        AppContext.BaseDirectory,
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));

    internal static string ResolveAppDataDirectory(string baseDirectory, string localAppData)
        => File.Exists(Path.Combine(baseDirectory, "portable.mode"))
            ? Path.Combine(baseDirectory, "data")
            : Path.Combine(localAppData, "Sweep");

    public static string DefaultPath => Path.Combine(AppDataDirectory, "sweep.db");

    /// <summary>
    /// 从 DiskSweep 更名到 Sweep 的一次性数据迁移：旧目录整体搬到新目录，库文件同步改名。
    /// 仅在非便携模式、新目录尚不存在、旧目录存在时执行；失败不阻断启动，旧数据保持原样。
    /// </summary>
    public static void MigrateLegacyData()
    {
        try
        {
            if (File.Exists(Path.Combine(AppContext.BaseDirectory, "portable.mode"))) return;
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var legacy = Path.Combine(local, "DiskSweep");
            var current = Path.Combine(local, "Sweep");
            if (!Directory.Exists(legacy) || Directory.Exists(current)) return;

            Directory.Move(legacy, current);
            var oldDb = Path.Combine(current, "disksweep.db");
            var newDb = Path.Combine(current, "sweep.db");
            if (File.Exists(oldDb) && !File.Exists(newDb)) File.Move(oldDb, newDb);
        }
        catch (Exception)
        {
            // 迁移失败时保持旧数据不动，程序仍以新路径全新启动，绝不删除旧目录。
        }
    }

    public SweepDb(string? databasePath = null)
    {
        DatabasePath = databasePath ?? DefaultPath;
        var dir = Path.GetDirectoryName(DatabasePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        // 不启用连接池：连接 Dispose 后必须立即释放文件句柄。
        // 清理工具需要支持数据库备份、迁移与旧库替换，被池化的连接会长期锁住 .db/.db-wal。
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        }.ToString();
    }

    public SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        return conn;
    }

    public void Initialize()
    {
        using var conn = Open();
        if (File.Exists(DatabasePath) && new FileInfo(DatabasePath).Length > 0)
        {
            var state = Scalar(conn, "PRAGMA integrity_check")?.ToString() ?? "";
            if (!string.Equals(state, "ok", StringComparison.OrdinalIgnoreCase))
            {
                conn.Close();
                var backup = DatabasePath + ".corrupt." + DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
                File.Move(DatabasePath, backup, true);
                conn.Open();
            }
        }

        Exec(conn, "PRAGMA journal_mode=WAL");
        Exec(conn, "PRAGMA synchronous=NORMAL");
        Exec(conn, "PRAGMA foreign_keys=ON");
        int version = Convert.ToInt32(Scalar(conn, "PRAGMA user_version") ?? 0, CultureInfo.InvariantCulture);
        if (version > CurrentSchemaVersion)
            throw new UnsupportedSchemaException($"数据库由更新版本创建（user_version={version}），已按只读方式拒绝反向迁移，请升级 Sweep。");

        if (version == 0)
        {
            CreateSchema(conn);
            Exec(conn, "PRAGMA user_version=1");
            version = 1;
        }
        // 旧库的 v1→v2 只为操作日志/撤销记录补列；这两张表已整体移除，直接升到 v3。
        if (version <= 2)
        {
            Exec(conn, "DROP TABLE IF EXISTS ops");
            Exec(conn, "DROP TABLE IF EXISTS undo_items");
            Exec(conn, "PRAGMA user_version=3");
        }
        Exec(conn, "PRAGMA wal_checkpoint(TRUNCATE)");
    }

    private static void CreateSchema(SqliteConnection conn)
    {
        Exec(conn, @"
CREATE TABLE IF NOT EXISTS scans (
  id TEXT PRIMARY KEY, root_path TEXT NOT NULL, mode TEXT NOT NULL,
  started_utc TEXT NOT NULL, finished_utc TEXT, total_files INTEGER, total_bytes INTEGER, denied_count INTEGER);

CREATE TABLE IF NOT EXISTS items (
  scan_id TEXT NOT NULL, path_hash TEXT NOT NULL, path TEXT NOT NULL,
  volume TEXT, file_id TEXT, logical_size INTEGER, alloc_size INTEGER,
  last_write TEXT, ext TEXT, is_dir INTEGER, is_alias INTEGER DEFAULT 0,
  PRIMARY KEY (scan_id, path_hash));

CREATE TABLE IF NOT EXISTS recommendations (
  scan_id TEXT NOT NULL, path_hash TEXT NOT NULL, source TEXT NOT NULL, action TEXT NOT NULL,
  risk TEXT NOT NULL, confidence REAL, reason TEXT, rule_id TEXT,
  requires_explicit_confirm INTEGER DEFAULT 0, PRIMARY KEY (scan_id, path_hash));

CREATE TABLE IF NOT EXISTS ai_cache (
  fingerprint TEXT PRIMARY KEY, provider TEXT NOT NULL, model TEXT NOT NULL,
  prompt_version TEXT NOT NULL, result_json TEXT NOT NULL, created_utc TEXT NOT NULL);

CREATE TABLE IF NOT EXISTS ai_usage (
  day TEXT NOT NULL, provider TEXT NOT NULL, requests INTEGER,
  input_tokens INTEGER, output_tokens INTEGER, est_cost REAL,
  PRIMARY KEY (day, provider));
");
    }

    public static string HashPath(string path) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(path.ToUpperInvariant())));

    public void InsertScan(ScanSnapshot snapshot)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"INSERT OR REPLACE INTO scans
          (id, root_path, mode, started_utc, finished_utc, total_files, total_bytes, denied_count)
          VALUES (@id,@root,@mode,@started,@finished,@files,@bytes,@denied)";
        Add(cmd, "@id", snapshot.SnapshotId);
        Add(cmd, "@root", snapshot.RootPath);
        Add(cmd, "@mode", snapshot.Mode == ScanMode.Mft ? "mft" : "walk");
        Add(cmd, "@started", Iso(snapshot.StartedUtc));
        Add(cmd, "@finished", snapshot.FinishedUtc is null ? DBNull.Value : Iso(snapshot.FinishedUtc.Value));
        Add(cmd, "@files", snapshot.TotalFiles);
        Add(cmd, "@bytes", snapshot.TotalBytes);
        Add(cmd, "@denied", snapshot.DeniedCount);
        cmd.ExecuteNonQuery();
    }

    public void FinishScan(string id, long files, long bytes, long denied)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE scans SET finished_utc=@t,total_files=@f,total_bytes=@b,denied_count=@d WHERE id=@id";
        Add(cmd, "@t", Iso(DateTime.UtcNow)); Add(cmd, "@f", files); Add(cmd, "@b", bytes);
        Add(cmd, "@d", denied); Add(cmd, "@id", id);
        cmd.ExecuteNonQuery();
    }

    /// <summary>批量写入，每 5k 条一个事务（文档 4.1.2）。</summary>
    public void InsertItemsBatched(string scanId, IEnumerable<CandidateItem> items)
    {
        using var conn = Open();
        var buffer = new List<CandidateItem>(5000);
        foreach (var item in items)
        {
            buffer.Add(item);
            if (buffer.Count == 5000) { WriteBatch(conn, scanId, buffer); buffer.Clear(); }
        }
        if (buffer.Count > 0) WriteBatch(conn, scanId, buffer);
    }

    private static void WriteBatch(SqliteConnection conn, string scanId, IReadOnlyList<CandidateItem> batch)
    {
        using var tx = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = @"INSERT OR REPLACE INTO items
          (scan_id,path_hash,path,volume,file_id,logical_size,alloc_size,last_write,ext,is_dir,is_alias)
          VALUES (@s,@h,@p,@v,@f,@ls,@as,@lw,@e,@d,@a)";
        foreach (var item in batch)
        {
            cmd.Parameters.Clear();
            Add(cmd, "@s", scanId); Add(cmd, "@h", HashPath(item.Path)); Add(cmd, "@p", item.Path);
            Add(cmd, "@v", item.VolumeSerial.ToString(CultureInfo.InvariantCulture));
            Add(cmd, "@f", item.FileId.ToString(CultureInfo.InvariantCulture));
            Add(cmd, "@ls", item.LogicalSize); Add(cmd, "@as", item.AllocationSize);
            Add(cmd, "@lw", Iso(DateTime.UtcNow.AddDays(-item.AgeDays)));
            Add(cmd, "@e", item.Extension); Add(cmd, "@d", item.IsDirectory ? 1 : 0);
            Add(cmd, "@a", item.IsAlias ? 1 : 0);
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    public void InsertRecommendations(string scanId, IEnumerable<PlanItem> plans)
    {
        using var conn = Open();
        using var tx = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = @"INSERT OR REPLACE INTO recommendations
          (scan_id,path_hash,source,action,risk,confidence,reason,rule_id,requires_explicit_confirm)
          VALUES (@s,@h,@src,@act,@risk,@conf,@reason,@rule,@explicit)";
        foreach (var p in plans)
        {
            cmd.Parameters.Clear();
            Add(cmd, "@s", scanId); Add(cmd, "@h", HashPath(p.Path));
            Add(cmd, "@src", p.Source.ToString().ToLowerInvariant());
            Add(cmd, "@act", p.Action.ToString().ToLowerInvariant());
            Add(cmd, "@risk", p.Risk.ToString().ToLowerInvariant());
            Add(cmd, "@conf", p.Recommendation.Confidence);
            Add(cmd, "@reason", p.Recommendation.Reason);
            Add(cmd, "@rule", (object?)p.Recommendation.RuleId ?? DBNull.Value);
            Add(cmd, "@explicit", p.RequiresExplicitConfirm ? 1 : 0);
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    public sealed record ScanSummary(string Id, DateTime StartedUtc, long Files, long Bytes, string Root);

    public IReadOnlyList<ScanSummary> GetRecentScans(int limit = 3)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT id,started_utc,COALESCE(total_files,0),COALESCE(total_bytes,0),root_path
                            FROM scans ORDER BY started_utc DESC LIMIT @n";
        Add(cmd, "@n", limit);
        using var reader = cmd.ExecuteReader();
        var list = new List<ScanSummary>();
        while (reader.Read())
            list.Add(new ScanSummary(reader.GetString(0), ParseIso(reader.GetString(1)),
                reader.GetInt64(2), reader.GetInt64(3), reader.GetString(4)));
        return list;
    }

    public string? GetAiCache(string fingerprint, TimeSpan maxAge)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT result_json,created_utc FROM ai_cache WHERE fingerprint=@f";
        Add(cmd, "@f", fingerprint);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return null;
        var created = ParseIso(reader.GetString(1));
        return DateTime.UtcNow - created > maxAge ? null : reader.GetString(0);
    }

    public void PutAiCache(string fingerprint, string provider, string model, string promptVersion, string resultJson, DateTime createdUtc)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"INSERT OR REPLACE INTO ai_cache (fingerprint,provider,model,prompt_version,result_json,created_utc)
                            VALUES (@f,@p,@m,@v,@j,@t)";
        Add(cmd, "@f", fingerprint); Add(cmd, "@p", provider); Add(cmd, "@m", model);
        Add(cmd, "@v", promptVersion); Add(cmd, "@j", resultJson); Add(cmd, "@t", Iso(createdUtc));
        cmd.ExecuteNonQuery();
    }

    public void PruneAiCache(int maxEntries, TimeSpan maxAge)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM ai_cache WHERE created_utc < @cutoff";
        Add(cmd, "@cutoff", Iso(DateTime.UtcNow.Subtract(maxAge)));
        cmd.ExecuteNonQuery();
        cmd.CommandText = @"DELETE FROM ai_cache WHERE fingerprint IN
            (SELECT fingerprint FROM ai_cache ORDER BY created_utc DESC LIMIT -1 OFFSET @keep)";
        cmd.Parameters.Clear(); Add(cmd, "@keep", maxEntries);
        cmd.ExecuteNonQuery();
    }

    public void AddAiUsage(DateTime utcNow, string provider, int requests, int inputTokens, int outputTokens, double estCostUsd)
    {
        var day = utcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"INSERT INTO ai_usage (day,provider,requests,input_tokens,output_tokens,est_cost)
          VALUES (@d,@p,@r,@i,@o,@c)
          ON CONFLICT(day,provider) DO UPDATE SET
            requests=requests+excluded.requests, input_tokens=input_tokens+excluded.input_tokens,
            output_tokens=output_tokens+excluded.output_tokens, est_cost=est_cost+excluded.est_cost";
        Add(cmd, "@d", day); Add(cmd, "@p", provider); Add(cmd, "@r", requests);
        Add(cmd, "@i", inputTokens); Add(cmd, "@o", outputTokens); Add(cmd, "@c", estCostUsd);
        cmd.ExecuteNonQuery();
    }

    public (double Cost, int Requests) GetMonthUsage(DateTime utcNow)
    {
        var prefix = utcNow.ToString("yyyy-MM", CultureInfo.InvariantCulture);
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COALESCE(SUM(est_cost),0),COALESCE(SUM(requests),0) FROM ai_usage WHERE day LIKE @p||'%'";
        Add(cmd, "@p", prefix);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? (reader.GetDouble(0), reader.GetInt32(1)) : (0, 0);
    }

    /// <summary>快照保留 3 份；接近 200 MB 先淘汰 AI 缓存（文档 5.3）。</summary>
    public void PruneOldData()
    {
        using var conn = Open();
        Exec(conn, @"DELETE FROM scans WHERE id NOT IN (SELECT id FROM scans ORDER BY started_utc DESC LIMIT 3)");
        Exec(conn, "DELETE FROM items WHERE scan_id NOT IN (SELECT id FROM scans)");
        Exec(conn, "DELETE FROM recommendations WHERE scan_id NOT IN (SELECT id FROM scans)");
    }

    public long GetDatabaseSizeBytes() => File.Exists(DatabasePath) ? new FileInfo(DatabasePath).Length : 0;

    public void VacuumIfNeeded(long limitBytes = 200L * 1024 * 1024)
    {
        if (GetDatabaseSizeBytes() < limitBytes) return;
        PruneAiCache(0, TimeSpan.Zero);
        PruneOldData();
        using var conn = Open();
        Exec(conn, "VACUUM");
        Exec(conn, "PRAGMA wal_checkpoint(TRUNCATE)");
    }

    public static string Iso(DateTime utc) => utc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    public static DateTime ParseIso(string value) =>
        DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dt)
            ? dt.ToUniversalTime() : DateTime.MinValue;

    private static void Add(SqliteCommand cmd, string name, object? value) =>
        cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);

    private static void Exec(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static object? Scalar(SqliteConnection conn, string sql, SqliteTransaction? tx = null)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        if (tx is not null) cmd.Transaction = tx;
        return cmd.ExecuteScalar();
    }
}
