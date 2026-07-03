using Microsoft.Data.Sqlite;

namespace SemanticPortrait.Core;

// Configuration + identity + telemetry rows: settings (API keys), profile, usage, alias map.
public sealed partial class Db
{
    // --- settings (encrypted key/value: API keys, model selection) ------------
    /// <summary>Returns a stored setting, or null if absent / DB not open.</summary>
    public string? GetSetting(string key)
    {
        lock (_gate)
        {
            if (_conn is null) return null;
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT value FROM settings WHERE key=$k;";
            cmd.Parameters.AddWithValue("$k", key);
            var v = cmd.ExecuteScalar();
            return v as string;
        }
    }

    /// <summary>Upsert a setting. An empty/null value deletes the row.</summary>
    public void SetSetting(string key, string? value)
    {
        lock (_gate)
        {
            if (_conn is null) return;
            using var cmd = _conn.CreateCommand();
            if (string.IsNullOrEmpty(value))
            {
                cmd.CommandText = "DELETE FROM settings WHERE key=$k;";
                cmd.Parameters.AddWithValue("$k", key);
            }
            else
            {
                cmd.CommandText = """
                    INSERT INTO settings(key, value) VALUES($k,$v)
                    ON CONFLICT(key) DO UPDATE SET value=$v;
                    """;
                cmd.Parameters.AddWithValue("$k", key);
                cmd.Parameters.AddWithValue("$v", value);
            }
            cmd.ExecuteNonQuery();
        }
    }

    // --- profile (identity facts; inside the encrypted DB) --------------------
    public string? GetProfileField(string key)
    {
        lock (_gate)
        {
            using var cmd = Conn.CreateCommand();
            cmd.CommandText = "SELECT value FROM profile WHERE key=$k;";
            cmd.Parameters.AddWithValue("$k", key);
            return cmd.ExecuteScalar() as string;
        }
    }

    public void SetProfileField(string key, string value)
    {
        lock (_gate)
        {
            using var cmd = Conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO profile(key, value) VALUES($k,$v)
                ON CONFLICT(key) DO UPDATE SET value=$v;
                """;
            cmd.Parameters.AddWithValue("$k", key);
            cmd.Parameters.AddWithValue("$v", value);
            cmd.ExecuteNonQuery();
        }
    }

    public Dictionary<string, string> AllProfileFields()
    {
        lock (_gate)
        {
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            using var cmd = Conn.CreateCommand();
            cmd.CommandText = "SELECT key, value FROM profile ORDER BY key;";
            using var r = cmd.ExecuteReader();
            while (r.Read()) d[r.GetString(0)] = r.GetString(1);
            return d;
        }
    }

    public void ClearProfile()
    {
        lock (_gate) { Exec("DELETE FROM profile;"); }
    }

    /// <summary>True when redirected to the isolated dev sandbox file (see OpenDevSandbox).</summary>
    public bool IsDevSandbox => _dbPath.EndsWith(".dev", StringComparison.OrdinalIgnoreCase);

    // --- usage / spend -------------------------------------------------------
    /// <summary>Current calendar month bucket (local time), e.g. "2026-06".</summary>
    public static string CurrentMonth() => DateTime.Now.ToString("yyyy-MM");

    /// <summary>Add one call's tokens + cost onto both the lifetime total and the current-month bucket.</summary>
    public void AddUsage(string model, long input, long output, double costUsd)
    {
        lock (_gate)
        {
            var now = DateTime.UtcNow.ToString("o");
            using (var cmd = Conn.CreateCommand())
            {
                cmd.CommandText = """
                    INSERT INTO usage(model, input_tok, output_tok, calls, cost_usd, updated_utc)
                    VALUES($m,$i,$o,1,$c,$u)
                    ON CONFLICT(model) DO UPDATE SET
                        input_tok  = input_tok  + $i,
                        output_tok = output_tok + $o,
                        calls      = calls      + 1,
                        cost_usd   = cost_usd   + $c,
                        updated_utc = $u;
                    """;
                cmd.Parameters.AddWithValue("$m", model);
                cmd.Parameters.AddWithValue("$i", input);
                cmd.Parameters.AddWithValue("$o", output);
                cmd.Parameters.AddWithValue("$c", costUsd);
                cmd.Parameters.AddWithValue("$u", now);
                cmd.ExecuteNonQuery();
            }
            using (var cmd = Conn.CreateCommand())
            {
                cmd.CommandText = """
                    INSERT INTO usage_monthly(period, model, input_tok, output_tok, calls, cost_usd, updated_utc)
                    VALUES($p,$m,$i,$o,1,$c,$u)
                    ON CONFLICT(period, model) DO UPDATE SET
                        input_tok  = input_tok  + $i,
                        output_tok = output_tok + $o,
                        calls      = calls      + 1,
                        cost_usd   = cost_usd   + $c,
                        updated_utc = $u;
                    """;
                cmd.Parameters.AddWithValue("$p", CurrentMonth());
                cmd.Parameters.AddWithValue("$m", model);
                cmd.Parameters.AddWithValue("$i", input);
                cmd.Parameters.AddWithValue("$o", output);
                cmd.Parameters.AddWithValue("$c", costUsd);
                cmd.Parameters.AddWithValue("$u", now);
                cmd.ExecuteNonQuery();
            }
        }
    }

    /// <summary>Lifetime totals summed across all models.</summary>
    public (long Input, long Output, long Calls, double CostUsd) GetUsageTotals()
    {
        lock (_gate)
        {
            using var cmd = Conn.CreateCommand();
            cmd.CommandText =
                "SELECT COALESCE(SUM(input_tok),0), COALESCE(SUM(output_tok),0), COALESCE(SUM(calls),0), COALESCE(SUM(cost_usd),0) FROM usage;";
            using var r = cmd.ExecuteReader();
            return r.Read() ? (r.GetInt64(0), r.GetInt64(1), r.GetInt64(2), r.GetDouble(3)) : (0, 0, 0, 0.0);
        }
    }

    /// <summary>Totals for one calendar month (period = "yyyy-MM").</summary>
    public (long Input, long Output, long Calls, double CostUsd) GetMonthlyTotals(string period)
    {
        lock (_gate)
        {
            using var cmd = Conn.CreateCommand();
            cmd.CommandText =
                "SELECT COALESCE(SUM(input_tok),0), COALESCE(SUM(output_tok),0), COALESCE(SUM(calls),0), COALESCE(SUM(cost_usd),0) FROM usage_monthly WHERE period=$p;";
            cmd.Parameters.AddWithValue("$p", period);
            using var r = cmd.ExecuteReader();
            return r.Read() ? (r.GetInt64(0), r.GetInt64(1), r.GetInt64(2), r.GetDouble(3)) : (0, 0, 0, 0.0);
        }
    }

    /// <summary>Lifetime spend broken down per model (highest cost first).</summary>
    public List<(string Model, long Input, long Output, long Calls, double CostUsd)> GetUsageByModel()
    {
        lock (_gate)
        {
            var list = new List<(string, long, long, long, double)>();
            using var cmd = Conn.CreateCommand();
            cmd.CommandText =
                "SELECT model, input_tok, output_tok, calls, cost_usd FROM usage ORDER BY cost_usd DESC;";
            using var r = cmd.ExecuteReader();
            while (r.Read())
                list.Add((r.GetString(0), r.GetInt64(1), r.GetInt64(2), r.GetInt64(3), r.GetDouble(4)));
            return list;
        }
    }

    // --- masking alias map ---------------------------------------------------
    /// <summary>Get the stable token for an original value, creating one (KIND_n) if new.</summary>
    public string GetOrCreateAlias(string kind, string original)
    {
        lock (_gate)
        {
            using (var sel = Conn.CreateCommand())
            {
                sel.CommandText = "SELECT token FROM aliases WHERE kind=$k AND original=$o;";
                sel.Parameters.AddWithValue("$k", kind);
                sel.Parameters.AddWithValue("$o", original);
                if (sel.ExecuteScalar() is string existing) return existing;
            }
            // Next number = highest existing suffix for this kind + 1 (COUNT(*)+1 collides with the
            // UNIQUE token constraint after any deletion).
            long n;
            using (var cnt = Conn.CreateCommand())
            {
                cnt.CommandText = """
                    SELECT COALESCE(MAX(CAST(substr(token, length($k) + 2) AS INTEGER)), 0) + 1
                    FROM aliases WHERE kind=$k;
                    """;
                cnt.Parameters.AddWithValue("$k", kind);
                n = Convert.ToInt64(cnt.ExecuteScalar() ?? 0L);
            }
            var token = $"{kind}_{n}";
            using var ins = Conn.CreateCommand();
            ins.CommandText = "INSERT INTO aliases(kind, original, token, created_utc) VALUES($k,$o,$t,$c);";
            ins.Parameters.AddWithValue("$k", kind);
            ins.Parameters.AddWithValue("$o", original);
            ins.Parameters.AddWithValue("$t", token);
            ins.Parameters.AddWithValue("$c", DateTime.UtcNow.ToString("o"));
            ins.ExecuteNonQuery();
            return token;
        }
    }

    /// <summary>All token→original pairs (for unmasking). Longest tokens first to avoid prefix clashes.</summary>
    public List<(string Token, string Original)> GetAliasReversals()
    {
        lock (_gate)
        {
            var list = new List<(string, string)>();
            using var cmd = Conn.CreateCommand();
            cmd.CommandText = "SELECT token, original FROM aliases ORDER BY length(token) DESC;";
            using var r = cmd.ExecuteReader();
            while (r.Read()) list.Add((r.GetString(0), r.GetString(1)));
            return list;
        }
    }
}
