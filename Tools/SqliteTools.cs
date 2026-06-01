using System.ComponentModel;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using ModelContextProtocol.Server;
using SQLMCPSharp.Services;

namespace SQLMCPSharp.Tools;

[McpServerToolType]
public sealed class SqliteTools
{
    [McpServerTool(Name = "sqlite_list_servers"),
     Description("**CALL FIRST** when you don't already know the SQLite aliases. Returns every configured connection's alias + description + the default alias picked when a tool's `alias` argument is omitted. Each alias is the handle every other sqlite_* tool accepts.")]
    public static string ListServers(SqlServerRegistry reg)
    {
        reg.RequireEnabled(SqlEngine.Sqlite);
        var aliases = reg.ListAliases(SqlEngine.Sqlite);
        return JsonSerializer.Serialize(new
        {
            defaultAlias = reg.DefaultAlias(SqlEngine.Sqlite),
            servers = aliases.Values.Select(s => new { alias = s.Alias, description = s.Description }),
        }, JsonOpts.Default);
    }

    [McpServerTool(Name = "sqlite_list_tables"),
     Description("List tables (and optionally views/indexes/triggers) in a SQLite database.")]
    public static async Task<string> ListTables(
        SqlServerRegistry reg,
        [Description("SQLite alias (sqlite_list_servers). Omit to use the default.")] string? alias = null,
        [Description("Object types to include: comma-separated subset of table,view,index,trigger. Default 'table,view'.")] string objectTypes = "table,view",
        CancellationToken ct = default)
    {
        await using var conn = (SqliteConnection)await reg.OpenAsync(SqlEngine.Sqlite, alias, ct).ConfigureAwait(false);
        var kinds = objectTypes.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(k => k.ToLowerInvariant()).Where(k => k is "table" or "view" or "index" or "trigger").ToArray();
        if (kinds.Length == 0) kinds = new[] { "table", "view" };
        var quoted = string.Join(", ", kinds.Select(k => $"'{k}'"));
        var sql = $"SELECT type, name, tbl_name FROM sqlite_master WHERE type IN ({quoted}) AND name NOT LIKE 'sqlite_%' ORDER BY type, name";
        var result = await SqlRunner.QueryAsync(conn, sql, reg.Options, ct).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { alias, kinds, count = result.RowCount, rows = result.Rows }, JsonOpts.Default);
    }

    [McpServerTool(Name = "sqlite_describe_table"),
     Description("Show column definitions, primary keys, indexes, and foreign keys for a SQLite table.")]
    public static async Task<string> DescribeTable(
        SqlServerRegistry reg,
        [Description("Table name.")] string table,
        [Description("SQLite alias. Omit for default.")] string? alias = null,
        CancellationToken ct = default)
    {
        await using var conn = (SqliteConnection)await reg.OpenAsync(SqlEngine.Sqlite, alias, ct).ConfigureAwait(false);
        var safe = table.Replace("'", "''");
        var columns = await SqlRunner.QueryAsync(conn, $"PRAGMA table_info('{safe}')", reg.Options, ct).ConfigureAwait(false);
        var indexes = await SqlRunner.QueryAsync(conn, $"PRAGMA index_list('{safe}')", reg.Options, ct).ConfigureAwait(false);
        var fks = await SqlRunner.QueryAsync(conn, $"PRAGMA foreign_key_list('{safe}')", reg.Options, ct).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { alias, table, columns = columns.Rows, indexes = indexes.Rows, foreignKeys = fks.Rows }, JsonOpts.Default);
    }

    [McpServerTool(Name = "sqlite_query"),
     Description("Run a SELECT against a SQLite database. Read-only path — always available regardless of Sql:ReadOnly. Capped at Sql:MaxRows rows.")]
    public static async Task<string> Query(
        SqlServerRegistry reg,
        [Description("SQL SELECT (or any read-only statement, e.g. PRAGMA).")] string sql,
        [Description("SQLite alias. Omit for default.")] string? alias = null,
        CancellationToken ct = default)
    {
        await using var conn = (SqliteConnection)await reg.OpenAsync(SqlEngine.Sqlite, alias, ct).ConfigureAwait(false);
        var result = await SqlRunner.QueryAsync(conn, sql, reg.Options, ct).ConfigureAwait(false);
        return JsonSerializer.Serialize(new
        {
            alias, columns = result.Columns, types = result.ColumnTypes, rowCount = result.RowCount,
            truncated = result.Truncated, cellsTruncated = result.CellsTruncated, rows = result.Rows,
        }, JsonOpts.Default);
    }

    [McpServerTool(Name = "sqlite_execute"),
     Description("Run an INSERT/UPDATE/DELETE/DDL statement against a SQLite database. Refused when Sql:ReadOnly=true.")]
    public static async Task<string> Execute(
        SqlServerRegistry reg,
        [Description("Non-SELECT SQL (INSERT, UPDATE, DELETE, CREATE TABLE, …).")] string sql,
        [Description("SQLite alias. Omit for default.")] string? alias = null,
        CancellationToken ct = default)
    {
        reg.RequireWritable("sqlite_execute");
        await using var conn = (SqliteConnection)await reg.OpenAsync(SqlEngine.Sqlite, alias, ct).ConfigureAwait(false);
        var rows = await SqlRunner.ExecuteAsync(conn, sql, reg.Options, ct, "sqlite", alias, "sqlite_execute").ConfigureAwait(false);
        return JsonSerializer.Serialize(new { alias, rowsAffected = rows }, JsonOpts.Default);
    }

    [McpServerTool(Name = "sqlite_search_objects"),
     Description("Search SQLite tables/views/indexes by name substring.")]
    public static async Task<string> SearchObjects(
        SqlServerRegistry reg,
        [Description("Substring to match (case-insensitive).")] string query,
        [Description("SQLite alias. Omit for default.")] string? alias = null,
        CancellationToken ct = default)
    {
        await using var conn = (SqliteConnection)await reg.OpenAsync(SqlEngine.Sqlite, alias, ct).ConfigureAwait(false);
        var pattern = "%" + query.Replace("'", "''") + "%";
        var result = await SqlRunner.QueryAsync(conn,
            $"SELECT type, name, tbl_name FROM sqlite_master WHERE name LIKE '{pattern}' COLLATE NOCASE AND name NOT LIKE 'sqlite_%' ORDER BY type, name",
            reg.Options, ct).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { alias, query, count = result.RowCount, rows = result.Rows }, JsonOpts.Default);
    }

    [McpServerTool(Name = "sqlite_get_definition"),
     Description("Return the CREATE statement for a table, view, index, or trigger from sqlite_master.")]
    public static async Task<string> GetDefinition(
        SqlServerRegistry reg,
        [Description("Object name.")] string name,
        [Description("SQLite alias. Omit for default.")] string? alias = null,
        CancellationToken ct = default)
    {
        await using var conn = (SqliteConnection)await reg.OpenAsync(SqlEngine.Sqlite, alias, ct).ConfigureAwait(false);
        var safe = name.Replace("'", "''");
        var rows = await SqlRunner.QueryAsync(conn, $"SELECT type, name, sql FROM sqlite_master WHERE name = '{safe}'", reg.Options, ct).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { alias, name, found = rows.RowCount, rows = rows.Rows }, JsonOpts.Default);
    }

    [McpServerTool(Name = "sqlite_vacuum"),
     Description("Run VACUUM on a SQLite database to compact the file and rebuild indexes. Refused when Sql:ReadOnly=true.")]
    public static async Task<string> Vacuum(
        SqlServerRegistry reg,
        [Description("SQLite alias. Omit for default.")] string? alias = null,
        CancellationToken ct = default)
    {
        reg.RequireWritable("sqlite_vacuum");
        await using var conn = (SqliteConnection)await reg.OpenAsync(SqlEngine.Sqlite, alias, ct).ConfigureAwait(false);
        await SqlRunner.ExecuteAsync(conn, "VACUUM", reg.Options, ct, "sqlite", alias, "sqlite_vacuum").ConfigureAwait(false);
        return JsonSerializer.Serialize(new { alias, vacuumed = true }, JsonOpts.Default);
    }

    // ───────────────────────────── diagnostics ─────────────────────────────

    [McpServerTool(Name = "sqlite_test_connection"),
     Description("Open the connection and run SELECT 1 — confirms credentials + database accessibility before issuing real queries. Reports elapsed ms.")]
    public static async Task<string> TestConnection(
        SqlServerRegistry reg,
        [Description("SQLite alias. Omit for default.")] string? alias = null,
        CancellationToken ct = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            await using var conn = (SqliteConnection)await reg.OpenAsync(SqlEngine.Sqlite, alias, ct).ConfigureAwait(false);
            await SqlRunner.QueryAsync(conn, "SELECT 1", reg.Options, ct).ConfigureAwait(false);
            sw.Stop();
            return JsonSerializer.Serialize(new { alias, ok = true, elapsedMs = sw.ElapsedMilliseconds }, JsonOpts.Default);
        }
        catch (Exception ex)
        {
            sw.Stop();
            return JsonSerializer.Serialize(new { alias, ok = false, elapsedMs = sw.ElapsedMilliseconds, error = ex.Message }, JsonOpts.Default);
        }
    }

    [McpServerTool(Name = "sqlite_server_info"),
     Description("Return SQLite library version, attached databases, and file size.")]
    public static async Task<string> ServerInfo(
        SqlServerRegistry reg,
        [Description("SQLite alias. Omit for default.")] string? alias = null,
        CancellationToken ct = default)
    {
        await using var conn = (SqliteConnection)await reg.OpenAsync(SqlEngine.Sqlite, alias, ct).ConfigureAwait(false);
        var version = (await SqlRunner.SelectStringsAsync(conn, "SELECT sqlite_version()", reg.Options, ct).ConfigureAwait(false)).FirstOrDefault();
        var dbs = await SqlRunner.QueryAsync(conn, "PRAGMA database_list", reg.Options, ct).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { alias, sqliteVersion = version, attachedDatabases = dbs.Rows }, JsonOpts.Default);
    }

    [McpServerTool(Name = "sqlite_explain"),
     Description("Run EXPLAIN QUERY PLAN against a SQL statement. Doesn't execute — useful for optimising queries.")]
    public static async Task<string> Explain(
        SqlServerRegistry reg,
        [Description("SQL to plan.")] string sql,
        [Description("SQLite alias. Omit for default.")] string? alias = null,
        CancellationToken ct = default)
    {
        await using var conn = (SqliteConnection)await reg.OpenAsync(SqlEngine.Sqlite, alias, ct).ConfigureAwait(false);
        var result = await SqlRunner.QueryAsync(conn, "EXPLAIN QUERY PLAN " + sql, reg.Options, ct).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { alias, plan = result.Rows }, JsonOpts.Default);
    }

    [McpServerTool(Name = "sqlite_query_with_params"),
     Description("Run a parameterised SELECT. SQL uses @name placeholders (e.g. \"SELECT * FROM Users WHERE id = @id\") and binds them from the params object. Avoids string-quote escaping bugs.")]
    public static async Task<string> QueryWithParams(
        SqlServerRegistry reg,
        [Description("SQL SELECT with @name placeholders.")] string sql,
        [Description("Parameter values: { \"@id\": 42, \"@name\": \"foo\" }. The leading @ is optional.")] Dictionary<string, object?> parameters,
        [Description("SQLite alias. Omit for default.")] string? alias = null,
        CancellationToken ct = default)
    {
        await using var conn = (SqliteConnection)await reg.OpenAsync(SqlEngine.Sqlite, alias, ct).ConfigureAwait(false);
        var result = await SqlRunner.QueryWithParamsAsync(conn, sql, parameters, reg.Options, ct).ConfigureAwait(false);
        return JsonSerializer.Serialize(new
        {
            alias, columns = result.Columns, types = result.ColumnTypes, rowCount = result.RowCount,
            truncated = result.Truncated, cellsTruncated = result.CellsTruncated, rows = result.Rows,
        }, JsonOpts.Default);
    }

    // SQLite is in-process / single-writer — no sessions or blocking. Diagnostics for those
    // are intentionally absent; the agent should reach for them on server engines only.

    [McpServerTool(Name = "sqlite_backup"),
     Description("Copy the live database to a file path using SQLite's online backup API. Refused when Sql:ReadOnly=true (a backup writes a new file).")]
    public static async Task<string> Backup(
        SqlServerRegistry reg,
        [Description("Target file path for the .db copy.")] string targetPath,
        [Description("SQLite alias. Omit for default.")] string? alias = null,
        CancellationToken ct = default)
    {
        reg.RequireWritable("sqlite_backup");
        await using var src = (SqliteConnection)await reg.OpenAsync(SqlEngine.Sqlite, alias, ct).ConfigureAwait(false);
        await using var dst = new SqliteConnection($"Data Source={targetPath}");
        await dst.OpenAsync(ct).ConfigureAwait(false);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            src.BackupDatabase(dst);
            sw.Stop();
            SqlAudit.Record("sqlite", alias, "sqlite_backup", $"BACKUP -> {targetPath}", rowsAffected: null, sw.ElapsedMilliseconds, success: true, error: null);
        }
        catch (Exception ex)
        {
            sw.Stop();
            SqlAudit.Record("sqlite", alias, "sqlite_backup", $"BACKUP -> {targetPath}", rowsAffected: null, sw.ElapsedMilliseconds, success: false, error: ex.Message);
            throw;
        }
        return JsonSerializer.Serialize(new { alias, targetPath }, JsonOpts.Default);
    }
}
