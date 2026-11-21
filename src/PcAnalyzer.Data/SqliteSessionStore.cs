using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using PcAnalyzer.Core.Abstractions;
using PcAnalyzer.Core.Analysis;
using PcAnalyzer.Core.Models;
using PcAnalyzer.Core.Serialization;

namespace PcAnalyzer.Data;

/// <summary>
/// Local SQLite storage (WAL mode). Telemetry samples are stored as compact JSON rows so new metrics
/// can be added without schema migrations; frame times are stored as float32 blobs.
/// </summary>
public sealed class SqliteSessionStore : ISessionStore
{
    private const int SchemaVersion = 1;
    private readonly string _path;
    private readonly string _cs;
    private readonly ILogger _log;
    private readonly SemaphoreSlim _write = new(1, 1);

    public SqliteSessionStore(string path, ILogger<SqliteSessionStore> log)
    {
        _path = path;
        _log = log;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        _cs = new SqliteConnectionStringBuilder { DataSource = path, Cache = SqliteCacheMode.Shared, Pooling = true }.ToString();
        Initialize();
    }

    public long DatabaseSizeBytes
    {
        get
        {
            long size = 0;
            foreach (var f in new[] { _path, _path + "-wal" })
                if (File.Exists(f)) size += new FileInfo(f).Length;
            return size;
        }
    }

    private SqliteConnection Open()
    {
        var c = new SqliteConnection(_cs);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;";
        cmd.ExecuteNonQuery();
        return c;
    }

    private void Initialize()
    {
        using var c = Open();
        Exec(c, "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;");
        Exec(c, """
            CREATE TABLE IF NOT EXISTS schema_version(version INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS sessions(
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                game_name TEXT NOT NULL,
                exe_name TEXT, exe_path TEXT,
                start_utc TEXT NOT NULL, end_utc TEXT,
                status TEXT NOT NULL, origin TEXT NOT NULL,
                label TEXT, notes TEXT,
                hardware_json TEXT, summary_json TEXT, report_json TEXT,
                raw_purged INTEGER NOT NULL DEFAULT 0);
            CREATE INDEX IF NOT EXISTS ix_sessions_game ON sessions(game_name);
            CREATE INDEX IF NOT EXISTS ix_sessions_start ON sessions(start_utc);
            CREATE TABLE IF NOT EXISTS telemetry(
                session_id INTEGER NOT NULL REFERENCES sessions(id) ON DELETE CASCADE,
                t_ms INTEGER NOT NULL, json TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS ix_telemetry_session ON telemetry(session_id, t_ms);
            CREATE TABLE IF NOT EXISTS frames(
                session_id INTEGER NOT NULL REFERENCES sessions(id) ON DELETE CASCADE,
                t_ms INTEGER NOT NULL, data BLOB NOT NULL);
            CREATE INDEX IF NOT EXISTS ix_frames_session ON frames(session_id, t_ms);
            CREATE TABLE IF NOT EXISTS events(
                session_id INTEGER NOT NULL REFERENCES sessions(id) ON DELETE CASCADE,
                t_ms INTEGER NOT NULL, json TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS ix_events_session ON events(session_id);
            """);
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT version FROM schema_version LIMIT 1";
        var v = cmd.ExecuteScalar();
        if (v == null) Exec(c, $"INSERT INTO schema_version(version) VALUES({SchemaVersion})");
        // Future migrations: if ((long)v < N) { ... }
    }

    private static void Exec(SqliteConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static long Ms(DateTime utc) => new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeMilliseconds();
    private static DateTime FromMs(long ms) => DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime;
    private static string Iso(DateTime utc) => DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToString("o", CultureInfo.InvariantCulture);
    private static DateTime ParseIso(string s) => DateTime.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);

    private async Task<T> Write<T>(Func<SqliteConnection, T> work)
    {
        await _write.WaitAsync();
        try
        {
            return await Task.Run(() =>
            {
                using var c = Open();
                return work(c);
            });
        }
        finally { _write.Release(); }
    }

    private static Task<T> Read<T>(Func<SqliteConnection, T> work, SqliteSessionStore self) =>
        Task.Run(() =>
        {
            using var c = self.Open();
            return work(c);
        });

    // ----------------------------------------------------------------- sessions

    public Task<long> CreateSessionAsync(GameSession s) => Write(c =>
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            INSERT INTO sessions(game_name, exe_name, exe_path, start_utc, end_utc, status, origin, label, notes, hardware_json)
            VALUES($g,$e,$p,$s,$end,$st,$o,$l,$n,$hw); SELECT last_insert_rowid();
            """;
        BindSession(cmd, s);
        return (long)cmd.ExecuteScalar()!;
    });

    public Task UpdateSessionAsync(GameSession s) => Write(c =>
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            UPDATE sessions SET game_name=$g, exe_name=$e, exe_path=$p, start_utc=$s, end_utc=$end, status=$st, origin=$o,
                label=$l, notes=$n, hardware_json=$hw, summary_json=$sum, report_json=$rep WHERE id=$id
            """;
        BindSession(cmd, s);
        cmd.Parameters.AddWithValue("$sum", (object?)(s.Summary == null ? null : Json.Serialize(s.Summary)) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$rep", (object?)(s.Report == null ? null : Json.Serialize(s.Report)) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$id", s.Id);
        return cmd.ExecuteNonQuery();
    });

    private static void BindSession(SqliteCommand cmd, GameSession s)
    {
        cmd.Parameters.AddWithValue("$g", s.GameName);
        cmd.Parameters.AddWithValue("$e", (object?)s.ExeName ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$p", (object?)s.ExePath ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$s", Iso(s.StartUtc));
        cmd.Parameters.AddWithValue("$end", s.EndUtc.HasValue ? Iso(s.EndUtc.Value) : DBNull.Value);
        cmd.Parameters.AddWithValue("$st", s.Status.ToString());
        cmd.Parameters.AddWithValue("$o", s.Origin.ToString());
        cmd.Parameters.AddWithValue("$l", (object?)s.Label ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$n", (object?)s.Notes ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$hw", (object?)(s.Hardware == null ? null : Json.Serialize(s.Hardware)) ?? DBNull.Value);
    }

    private const string SessionColumns = "id, game_name, exe_name, exe_path, start_utc, end_utc, status, origin, label, notes, hardware_json, summary_json, report_json";

    private GameSession ReadSession(SqliteDataReader r, bool withReport)
    {
        var s = new GameSession
        {
            Id = r.GetInt64(0),
            GameName = r.GetString(1),
            ExeName = r.IsDBNull(2) ? null : r.GetString(2),
            ExePath = r.IsDBNull(3) ? null : r.GetString(3),
            StartUtc = ParseIso(r.GetString(4)),
            EndUtc = r.IsDBNull(5) ? null : ParseIso(r.GetString(5)),
            Status = Enum.TryParse<SessionStatus>(r.GetString(6), out var st) ? st : SessionStatus.Completed,
            Origin = Enum.TryParse<SessionOrigin>(r.GetString(7), out var o) ? o : SessionOrigin.Manual,
            Label = r.IsDBNull(8) ? null : r.GetString(8),
            Notes = r.IsDBNull(9) ? null : r.GetString(9),
        };
        try
        {
            if (!r.IsDBNull(10)) s.Hardware = Json.Deserialize<SystemInfo>(r.GetString(10));
            if (!r.IsDBNull(11)) s.Summary = Json.Deserialize<SessionSummary>(r.GetString(11));
            if (withReport && !r.IsDBNull(12)) s.Report = Json.Deserialize<SessionReport>(r.GetString(12));
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Corrupted JSON in session {Id}", s.Id);
        }
        return s;
    }

    public Task<GameSession?> GetSessionAsync(long id) => Read(c =>
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT {SessionColumns} FROM sessions WHERE id=$id";
        cmd.Parameters.AddWithValue("$id", id);
        using var r = cmd.ExecuteReader();
        return r.Read() ? ReadSession(r, true) : null;
    }, this);

    public Task<List<GameSession>> ListSessionsAsync(string? game = null, int limit = 500) => Read(c =>
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT {SessionColumns} FROM sessions {(game != null ? "WHERE game_name=$g" : "")} ORDER BY start_utc DESC LIMIT $lim";
        if (game != null) cmd.Parameters.AddWithValue("$g", game);
        cmd.Parameters.AddWithValue("$lim", limit);
        var list = new List<GameSession>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(ReadSession(r, true));
        return list;
    }, this);

    public Task<List<string>> ListGamesAsync() => Read(c =>
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT game_name FROM sessions GROUP BY game_name ORDER BY MAX(start_utc) DESC";
        var list = new List<string>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(r.GetString(0));
        return list;
    }, this);

    public Task<List<GameSession>> GetUnfinishedAsync() => Read(c =>
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT {SessionColumns} FROM sessions WHERE status='{SessionStatus.Recording}'";
        var list = new List<GameSession>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(ReadSession(r, false));
        return list;
    }, this);

    public Task DeleteSessionAsync(long id) => Write(c =>
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM sessions WHERE id=$id";
        cmd.Parameters.AddWithValue("$id", id);
        return cmd.ExecuteNonQuery();
    });

    // ----------------------------------------------------------------- telemetry

    public Task AppendTelemetryAsync(long sessionId, IReadOnlyList<TelemetrySample> samples)
    {
        if (samples.Count == 0) return Task.CompletedTask;
        return Write(c =>
        {
            using var tx = c.BeginTransaction();
            using var cmd = c.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT INTO telemetry(session_id, t_ms, json) VALUES($s,$t,$j)";
            var ps = cmd.Parameters.Add("$s", SqliteType.Integer);
            var pt = cmd.Parameters.Add("$t", SqliteType.Integer);
            var pj = cmd.Parameters.Add("$j", SqliteType.Text);
            ps.Value = sessionId;
            foreach (var s in samples)
            {
                pt.Value = Ms(s.TimestampUtc);
                pj.Value = Json.Serialize(s);
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
            return 0;
        });
    }

    public Task AppendFramesAsync(long sessionId, IReadOnlyList<FrameChunk> chunks)
    {
        if (chunks.Count == 0) return Task.CompletedTask;
        return Write(c =>
        {
            using var tx = c.BeginTransaction();
            using var cmd = c.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT INTO frames(session_id, t_ms, data) VALUES($s,$t,$d)";
            var ps = cmd.Parameters.Add("$s", SqliteType.Integer);
            var pt = cmd.Parameters.Add("$t", SqliteType.Integer);
            var pd = cmd.Parameters.Add("$d", SqliteType.Blob);
            ps.Value = sessionId;
            foreach (var ch in chunks)
            {
                pt.Value = Ms(ch.TimestampUtc);
                pd.Value = MemoryMarshal.AsBytes(ch.FrameTimesMs.AsSpan()).ToArray();
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
            return 0;
        });
    }

    public Task AppendEventAsync(long sessionId, LiveEvent ev) => Write(c =>
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO events(session_id, t_ms, json) VALUES($s,$t,$j)";
        cmd.Parameters.AddWithValue("$s", sessionId);
        cmd.Parameters.AddWithValue("$t", Ms(ev.TimestampUtc));
        cmd.Parameters.AddWithValue("$j", Json.Serialize(ev));
        return cmd.ExecuteNonQuery();
    });

    public Task<List<TelemetrySample>> LoadTelemetryAsync(long sessionId) => Read(c =>
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT json FROM telemetry WHERE session_id=$s ORDER BY t_ms";
        cmd.Parameters.AddWithValue("$s", sessionId);
        var list = new List<TelemetrySample>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            try { if (Json.Deserialize<TelemetrySample>(r.GetString(0)) is { } s) list.Add(s); }
            catch (Exception ex) { _log.LogWarning(ex, "Skipping corrupted sample in session {Id}", sessionId); }
        }
        return list;
    }, this);

    public Task<List<FrameChunk>> LoadFramesAsync(long sessionId) => Read(c =>
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT t_ms, data FROM frames WHERE session_id=$s ORDER BY t_ms";
        cmd.Parameters.AddWithValue("$s", sessionId);
        var list = new List<FrameChunk>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var bytes = (byte[])r[1];
            list.Add(new FrameChunk(FromMs(r.GetInt64(0)), MemoryMarshal.Cast<byte, float>(bytes).ToArray()));
        }
        return list;
    }, this);

    public Task<List<LiveEvent>> LoadEventsAsync(long sessionId) => Read(c =>
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT json FROM events WHERE session_id=$s ORDER BY t_ms";
        cmd.Parameters.AddWithValue("$s", sessionId);
        var list = new List<LiveEvent>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) if (Json.Deserialize<LiveEvent>(r.GetString(0)) is { } e) list.Add(e);
        return list;
    }, this);

    // ----------------------------------------------------------------- retention

    public Task<RetentionResult> RunRetentionAsync(AppSettings settings) => Write(c =>
    {
        int deleted = 0, purged = 0;
        if (settings.HistoryRetentionDays > 0)
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = "DELETE FROM sessions WHERE status<>'Recording' AND start_utc < $cut";
            cmd.Parameters.AddWithValue("$cut", Iso(DateTime.UtcNow.AddDays(-settings.HistoryRetentionDays)));
            deleted = cmd.ExecuteNonQuery();
        }
        if (settings.RawTelemetryRetentionDays > 0)
            purged += PurgeRaw(c, "start_utc < $cut", ("$cut", Iso(DateTime.UtcNow.AddDays(-settings.RawTelemetryRetentionDays))));

        // Size cap: drop raw data of the oldest sessions until under the limit (summaries/reports stay).
        long limit = (long)settings.MaxDatabaseMb * 1024 * 1024;
        int guard = 0;
        while (limit > 0 && DatabaseSizeBytes > limit && guard++ < 50)
        {
            using var q = c.CreateCommand();
            q.CommandText = "SELECT id FROM sessions WHERE raw_purged=0 AND status<>'Recording' ORDER BY start_utc LIMIT 1";
            if (q.ExecuteScalar() is not long id) break;
            purged += PurgeRaw(c, "id=$id", ("$id", id));
            Exec(c, "PRAGMA wal_checkpoint(TRUNCATE); VACUUM;");
        }
        if (deleted + purged > 0) Exec(c, "PRAGMA wal_checkpoint(TRUNCATE);");
        _log.LogInformation("Retention: {Deleted} sessions deleted, {Purged} raw purged", deleted, purged);
        return new RetentionResult(deleted, purged);
    });

    private static int PurgeRaw(SqliteConnection c, string where, (string, object) p)
    {
        using var tx = c.BeginTransaction();
        using var ids = c.CreateCommand();
        ids.Transaction = tx;
        ids.CommandText = $"SELECT id FROM sessions WHERE raw_purged=0 AND status<>'Recording' AND {where}";
        ids.Parameters.AddWithValue(p.Item1, p.Item2);
        var list = new List<long>();
        using (var r = ids.ExecuteReader()) while (r.Read()) list.Add(r.GetInt64(0));
        foreach (var id in list)
        {
            using var d = c.CreateCommand();
            d.Transaction = tx;
            d.CommandText = "DELETE FROM telemetry WHERE session_id=$id; DELETE FROM frames WHERE session_id=$id; UPDATE sessions SET raw_purged=1 WHERE id=$id;";
            d.Parameters.AddWithValue("$id", id);
            d.ExecuteNonQuery();
        }
        tx.Commit();
        return list.Count;
    }
}
