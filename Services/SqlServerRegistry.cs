using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using MySqlConnector;
using Oracle.ManagedDataAccess.Client;
using SQLMCPSharp.Configuration;

namespace SQLMCPSharp.Services;

/// <summary>
/// One <see cref="SqlServerRegistry"/> owns every configured SQL connection across every engine.
/// Tools call <see cref="Open"/> with an engine + optional alias to get an open
/// <see cref="DbConnection"/> they can run commands against. Auto-naming kicks in at construction:
/// any entry in config that omits its alias gets a stable <c>&lt;engine&gt;-N</c> name so the
/// agent always has something to refer to. Default-alias resolution falls back to the first
/// enabled entry per engine.
/// </summary>
public sealed class SqlServerRegistry
{
    private readonly SqlOptions _options;
    private readonly Dictionary<SqlEngine, Dictionary<string, SqlServerEntry>> _byEngine;
    private readonly Dictionary<SqlEngine, string?> _defaultAlias;

    public SqlServerRegistry(IOptions<SqlOptions> options)
    {
        _options = options.Value;

        // Build per-engine alias → entry maps once. Mutations to options after this point won't
        // be reflected; for runtime CRUD we'd add a Reload method later.
        _byEngine = new Dictionary<SqlEngine, Dictionary<string, SqlServerEntry>>();
        _defaultAlias = new Dictionary<SqlEngine, string?>();
        foreach (var (engine, bundle) in EnumerateBundles())
        {
            var map = new Dictionary<string, SqlServerEntry>(StringComparer.OrdinalIgnoreCase);
            int idx = 1;
            foreach (var entry in bundle.Servers)
            {
                var alias = string.IsNullOrWhiteSpace(entry.Alias)
                    ? $"{engine.ToString().ToLowerInvariant()}-{idx}"
                    : entry.Alias;
                while (map.ContainsKey(alias)) alias = $"{engine.ToString().ToLowerInvariant()}-{++idx}";
                // Reflect the resolved alias back onto the entry so list_servers reads it cleanly.
                entry.Alias = alias;
                map[alias] = entry;
                idx++;
            }
            _byEngine[engine] = map;

            var preferred = bundle.DefaultAlias;
            _defaultAlias[engine] = !string.IsNullOrWhiteSpace(preferred) && map.ContainsKey(preferred)
                ? preferred
                : map.Keys.FirstOrDefault();
        }
    }

    public SqlOptions Options => _options;
    public bool IsReadOnly => _options.ReadOnly;
    public bool AllowDangerous => _options.AllowDangerous;

    public bool IsEnabled(SqlEngine engine) => BundleFor(engine).Enabled;

    public IReadOnlyDictionary<string, SqlServerEntry> ListAliases(SqlEngine engine) => _byEngine[engine];

    public string? DefaultAlias(SqlEngine engine) => _defaultAlias[engine];

    /// <summary>Throws if the engine isn't enabled — every per-engine tool calls this first.</summary>
    public void RequireEnabled(SqlEngine engine)
    {
        if (!IsEnabled(engine))
            throw new InvalidOperationException(
                $"{engine} engine is disabled. Set Sql:{engine}:Enabled=true to expose its tools.");
    }

    public void RequireWritable(string operation)
    {
        if (_options.ReadOnly)
            throw new InvalidOperationException(
                $"Operation '{operation}' is blocked: server is running in read-only mode (Sql:ReadOnly=true).");
    }

    public void RequireDangerous(string operation)
    {
        if (!_options.AllowDangerous)
            throw new InvalidOperationException(
                $"Operation '{operation}' is dangerous (drops / destructive grants) and disabled. Set Sql:AllowDangerous=true to permit it.");
    }

    /// <summary>
    /// Open and return a connection for the given engine + optional alias. Caller owns it
    /// and is responsible for disposing.
    /// </summary>
    public async Task<DbConnection> OpenAsync(SqlEngine engine, string? alias, CancellationToken ct)
    {
        RequireEnabled(engine);
        var resolved = ResolveAlias(engine, alias);
        DbConnection conn = engine switch
        {
            SqlEngine.Mssql => new SqlConnection(resolved.ConnectionString),
            SqlEngine.Mysql => new MySqlConnection(resolved.ConnectionString),
            SqlEngine.Sqlite => new SqliteConnection(resolved.ConnectionString),
            SqlEngine.Oracle => new OracleConnection(resolved.ConnectionString),
            _ => throw new ArgumentOutOfRangeException(nameof(engine)),
        };
        await conn.OpenAsync(ct).ConfigureAwait(false);
        return conn;
    }

    public SqlServerEntry ResolveAlias(SqlEngine engine, string? alias)
    {
        var map = _byEngine[engine];
        if (map.Count == 0) throw new InvalidOperationException($"No {engine} servers configured.");

        if (!string.IsNullOrWhiteSpace(alias))
        {
            if (map.TryGetValue(alias, out var direct)) return direct;
            throw new InvalidOperationException(
                $"Unknown {engine} alias '{alias}'. Available: {string.Join(", ", map.Keys)}.");
        }

        var def = _defaultAlias[engine];
        if (def is null) throw new InvalidOperationException($"No default alias for {engine}.");
        return map[def];
    }

    private EngineBundle BundleFor(SqlEngine engine) => engine switch
    {
        SqlEngine.Mssql => _options.Mssql,
        SqlEngine.Mysql => _options.Mysql,
        SqlEngine.Sqlite => _options.Sqlite,
        SqlEngine.Oracle => _options.Oracle,
        _ => throw new ArgumentOutOfRangeException(nameof(engine)),
    };

    private IEnumerable<(SqlEngine engine, EngineBundle bundle)> EnumerateBundles()
    {
        yield return (SqlEngine.Mssql, _options.Mssql);
        yield return (SqlEngine.Mysql, _options.Mysql);
        yield return (SqlEngine.Sqlite, _options.Sqlite);
        yield return (SqlEngine.Oracle, _options.Oracle);
    }
}

public enum SqlEngine { Mssql, Mysql, Sqlite, Oracle }
