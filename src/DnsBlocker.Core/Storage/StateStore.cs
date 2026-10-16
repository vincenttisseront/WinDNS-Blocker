using DnsBlocker.Core.Models;
using DnsBlocker.Core.Paths;
using Microsoft.Data.Sqlite;

namespace DnsBlocker.Core.Storage;

public sealed class StateStore : IDisposable
{
    private readonly string _connectionString;
    private readonly object _gate = new();

    public StateStore(string? dbPath = null)
    {
        AppPaths.EnsureDirectories();
        var path = dbPath ?? AppPaths.StateDbPath;
        _connectionString = new SqliteConnectionStringBuilder { DataSource = path }.ToString();
        Initialize();
    }

    private void Initialize()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS domains (
              domain TEXT PRIMARY KEY COLLATE NOCASE
            );
            CREATE TABLE IF NOT EXISTS target_status (
              target TEXT PRIMARY KEY COLLATE NOCASE,
              status TEXT NOT NULL,
              error TEXT,
              policy_count INTEGER NOT NULL DEFAULT 0,
              domain_count INTEGER NOT NULL DEFAULT 0,
              finished_at TEXT,
              duration_ms INTEGER
            );
            CREATE TABLE IF NOT EXISTS sync_meta (
              id INTEGER PRIMARY KEY CHECK (id = 1),
              started_at TEXT,
              finished_at TEXT,
              domain_count INTEGER,
              whitelist_count INTEGER,
              source_list_count INTEGER,
              error TEXT
            );
            CREATE TABLE IF NOT EXISTS query_events (
              id INTEGER PRIMARY KEY AUTOINCREMENT,
              ts TEXT NOT NULL,
              client_ip TEXT,
              query_name TEXT NOT NULL,
              blocked INTEGER NOT NULL DEFAULT 0
            );
            CREATE INDEX IF NOT EXISTS idx_query_events_ts ON query_events(ts);
            CREATE INDEX IF NOT EXISTS idx_query_events_blocked ON query_events(blocked);
            """;
        cmd.ExecuteNonQuery();
    }

    public void ReplaceDomains(IEnumerable<string> domains)
    {
        lock (_gate)
        {
            using var conn = Open();
            using var tx = conn.BeginTransaction();
            using (var clear = conn.CreateCommand())
            {
                clear.Transaction = tx;
                clear.CommandText = "DELETE FROM domains;";
                clear.ExecuteNonQuery();
            }

            using var insert = conn.CreateCommand();
            insert.Transaction = tx;
            insert.CommandText = "INSERT OR IGNORE INTO domains(domain) VALUES ($d);";
            var p = insert.CreateParameter();
            p.ParameterName = "$d";
            insert.Parameters.Add(p);

            foreach (var domain in domains)
            {
                p.Value = domain;
                insert.ExecuteNonQuery();
            }

            tx.Commit();
        }
    }

    public int CountDomains()
    {
        lock (_gate)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM domains;";
            return Convert.ToInt32(cmd.ExecuteScalar());
        }
    }

    public IReadOnlyList<string> SearchDomains(string? query, int limit = 100)
    {
        lock (_gate)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            if (string.IsNullOrWhiteSpace(query))
            {
                cmd.CommandText = "SELECT domain FROM domains ORDER BY domain LIMIT $limit;";
                cmd.Parameters.AddWithValue("$limit", limit);
            }
            else
            {
                cmd.CommandText = "SELECT domain FROM domains WHERE domain LIKE $q ORDER BY domain LIMIT $limit;";
                cmd.Parameters.AddWithValue("$q", "%" + query.Trim() + "%");
                cmd.Parameters.AddWithValue("$limit", limit);
            }

            var list = new List<string>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                list.Add(reader.GetString(0));
            return list;
        }
    }

    public bool IsBlocked(string domain)
    {
        lock (_gate)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT 1 FROM domains WHERE domain = $d LIMIT 1;";
            cmd.Parameters.AddWithValue("$d", domain.Trim('.').ToLowerInvariant());
            return cmd.ExecuteScalar() is not null;
        }
    }

    public void SaveSyncResult(SyncRunResult result)
    {
        lock (_gate)
        {
            using var conn = Open();
            using var tx = conn.BeginTransaction();

            using (var meta = conn.CreateCommand())
            {
                meta.Transaction = tx;
                meta.CommandText = """
                    INSERT INTO sync_meta(id, started_at, finished_at, domain_count, whitelist_count, source_list_count, error)
                    VALUES (1, $s, $f, $d, $w, $src, $e)
                    ON CONFLICT(id) DO UPDATE SET
                      started_at = excluded.started_at,
                      finished_at = excluded.finished_at,
                      domain_count = excluded.domain_count,
                      whitelist_count = excluded.whitelist_count,
                      source_list_count = excluded.source_list_count,
                      error = excluded.error;
                    """;
                meta.Parameters.AddWithValue("$s", result.StartedAt.ToString("O"));
                meta.Parameters.AddWithValue("$f", (object?)result.FinishedAt?.ToString("O") ?? DBNull.Value);
                meta.Parameters.AddWithValue("$d", result.DomainCount);
                meta.Parameters.AddWithValue("$w", result.WhitelistCount);
                meta.Parameters.AddWithValue("$src", result.SourceListCount);
                meta.Parameters.AddWithValue("$e", (object?)result.Error ?? DBNull.Value);
                meta.ExecuteNonQuery();
            }

            using (var clear = conn.CreateCommand())
            {
                clear.Transaction = tx;
                clear.CommandText = "DELETE FROM target_status;";
                clear.ExecuteNonQuery();
            }

            foreach (var t in result.Targets)
            {
                using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = """
                    INSERT INTO target_status(target, status, error, policy_count, domain_count, finished_at, duration_ms)
                    VALUES ($t, $s, $e, $p, $d, $f, $dur);
                    """;
                cmd.Parameters.AddWithValue("$t", t.Target);
                cmd.Parameters.AddWithValue("$s", t.Status.ToString());
                cmd.Parameters.AddWithValue("$e", (object?)t.Error ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$p", t.PolicyCount);
                cmd.Parameters.AddWithValue("$d", t.DomainCount);
                cmd.Parameters.AddWithValue("$f", (object?)t.FinishedAt?.ToString("O") ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$dur", (object?)t.Duration?.TotalMilliseconds ?? DBNull.Value);
                cmd.ExecuteNonQuery();
            }

            tx.Commit();
        }
    }

    public SyncRunResult? LoadLastSync()
    {
        lock (_gate)
        {
            using var conn = Open();
            using var meta = conn.CreateCommand();
            meta.CommandText = "SELECT started_at, finished_at, domain_count, whitelist_count, source_list_count, error FROM sync_meta WHERE id = 1;";
            using var reader = meta.ExecuteReader();
            if (!reader.Read())
                return null;

            var result = new SyncRunResult
            {
                StartedAt = DateTimeOffset.Parse(reader.GetString(0)),
                FinishedAt = reader.IsDBNull(1) ? null : DateTimeOffset.Parse(reader.GetString(1)),
                DomainCount = reader.GetInt32(2),
                WhitelistCount = reader.GetInt32(3),
                SourceListCount = reader.GetInt32(4),
                Error = reader.IsDBNull(5) ? null : reader.GetString(5)
            };
            reader.Close();

            using var targets = conn.CreateCommand();
            targets.CommandText = "SELECT target, status, error, policy_count, domain_count, finished_at, duration_ms FROM target_status ORDER BY target;";
            using var tr = targets.ExecuteReader();
            while (tr.Read())
            {
                result.Targets.Add(new TargetSyncResult
                {
                    Target = tr.GetString(0),
                    Status = Enum.Parse<TargetSyncStatus>(tr.GetString(1)),
                    Error = tr.IsDBNull(2) ? null : tr.GetString(2),
                    PolicyCount = tr.GetInt32(3),
                    DomainCount = tr.GetInt32(4),
                    FinishedAt = tr.IsDBNull(5) ? null : DateTimeOffset.Parse(tr.GetString(5)),
                    Duration = tr.IsDBNull(6) ? null : TimeSpan.FromMilliseconds(tr.GetDouble(6))
                });
            }

            return result;
        }
    }

    public void InsertQueryEvent(DateTimeOffset ts, string? clientIp, string queryName, bool blocked)
    {
        lock (_gate)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "INSERT INTO query_events(ts, client_ip, query_name, blocked) VALUES ($ts, $ip, $q, $b);";
            cmd.Parameters.AddWithValue("$ts", ts.ToString("O"));
            cmd.Parameters.AddWithValue("$ip", (object?)clientIp ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$q", queryName);
            cmd.Parameters.AddWithValue("$b", blocked ? 1 : 0);
            cmd.ExecuteNonQuery();
        }
    }

    public StatsSnapshot GetStats(DateTimeOffset fromUtc, DateTimeOffset toUtc, int topN = 20)
    {
        topN = Math.Clamp(topN, 1, 100);
        lock (_gate)
        {
            using var conn = Open();
            long total;
            long blocked;
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT COUNT(*), COALESCE(SUM(blocked),0) FROM query_events WHERE ts >= $f AND ts <= $t;";
                cmd.Parameters.AddWithValue("$f", fromUtc.ToString("O"));
                cmd.Parameters.AddWithValue("$t", toUtc.ToString("O"));
                using var r = cmd.ExecuteReader();
                r.Read();
                total = r.GetInt64(0);
                blocked = r.GetInt64(1);
            }

            var topQueried = QueryTop(conn, """
                SELECT query_name, COUNT(*) AS c FROM query_events
                WHERE ts >= $f AND ts <= $t
                GROUP BY query_name ORDER BY c DESC LIMIT $limit;
                """, fromUtc, toUtc, topN);

            var topBlocked = QueryTop(conn, """
                SELECT query_name, COUNT(*) AS c FROM query_events
                WHERE blocked = 1 AND ts >= $f AND ts <= $t
                GROUP BY query_name ORDER BY c DESC LIMIT $limit;
                """, fromUtc, toUtc, topN);

            var topClients = QueryTop(conn, """
                SELECT COALESCE(client_ip, 'unknown'), COUNT(*) AS c FROM query_events
                WHERE blocked = 1 AND ts >= $f AND ts <= $t
                GROUP BY COALESCE(client_ip, 'unknown') ORDER BY c DESC LIMIT $limit;
                """, fromUtc, toUtc, topN);

            return new StatsSnapshot
            {
                FromUtc = fromUtc,
                ToUtc = toUtc,
                TotalQueries = total,
                BlockedQueries = blocked,
                TopN = topN,
                TopQueriedDomains = topQueried,
                TopBlockedDomains = topBlocked,
                TopClients = topClients,
                Message = total == 0
                    ? "No query events yet. Enable DNS analytical logging and stats collection."
                    : null
            };
        }
    }

    private static List<StatRow> QueryTop(
        SqliteConnection conn,
        string sql,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        int limit)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("$f", fromUtc.ToString("O"));
        cmd.Parameters.AddWithValue("$t", toUtc.ToString("O"));
        cmd.Parameters.AddWithValue("$limit", limit);
        var rows = new List<StatRow>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            rows.Add(new StatRow { Key = reader.GetString(0), Count = reader.GetInt64(1) });
        return rows;
    }

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        return conn;
    }

    public void Dispose()
    {
        // no pooled connection held
    }
}
