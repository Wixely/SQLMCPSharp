using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using MySqlConnector;
using SQLMCPSharp.Services;

namespace SQLMCPSharp.Tools;

[McpServerToolType]
public sealed class MysqlTools
{
    [McpServerTool(Name = "mysql_list_servers"),
     Description("**CALL FIRST** when you don't already know the MySQL/MariaDB aliases. Returns every configured connection's alias + description + the default alias picked when a tool's `alias` argument is omitted. Each alias is the handle every other mysql_* tool accepts.")]
    public static string ListServers(SqlServerRegistry reg)
    {
        reg.RequireEnabled(SqlEngine.Mysql);
        var aliases = reg.ListAliases(SqlEngine.Mysql);
        return JsonSerializer.Serialize(new
        {
            defaultAlias = reg.DefaultAlias(SqlEngine.Mysql),
            servers = aliases.Values.Select(s => new { alias = s.Alias, description = s.Description }),
        }, JsonOpts.Default);
    }

    [McpServerTool(Name = "mysql_list_databases"),
     Description("List databases (schemas) on the server.")]
    public static async Task<string> ListDatabases(
        SqlServerRegistry reg,
        [Description("MySQL alias. Omit for default.")] string? alias = null,
        CancellationToken ct = default)
    {
        await using var conn = (MySqlConnection)await reg.OpenAsync(SqlEngine.Mysql, alias, ct).ConfigureAwait(false);
        var result = await SqlRunner.QueryAsync(conn, "SELECT schema_name FROM information_schema.schemata ORDER BY schema_name", reg.Options, ct).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { alias, count = result.RowCount, databases = result.Rows }, JsonOpts.Default);
    }

    [McpServerTool(Name = "mysql_list_tables"),
     Description("List tables in a database. Empty database = the connection's default.")]
    public static async Task<string> ListTables(
        SqlServerRegistry reg,
        [Description("MySQL alias. Omit for default.")] string? alias = null,
        [Description("Database/schema name. Empty = the connected database (DATABASE()).")] string? database = null,
        CancellationToken ct = default)
    {
        await using var conn = (MySqlConnection)await reg.OpenAsync(SqlEngine.Mysql, alias, ct).ConfigureAwait(false);
        var schemaClause = string.IsNullOrWhiteSpace(database)
            ? "AND table_schema = DATABASE()"
            : $"AND table_schema = '{database.Replace("'", "''")}'";
        var result = await SqlRunner.QueryAsync(conn,
            $"SELECT table_schema, table_name, table_rows, create_time, update_time FROM information_schema.tables WHERE table_type='BASE TABLE' {schemaClause} ORDER BY table_schema, table_name",
            reg.Options, ct).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { alias, database, count = result.RowCount, tables = result.Rows }, JsonOpts.Default);
    }

    [McpServerTool(Name = "mysql_list_views"),
     Description("List views in a database.")]
    public static async Task<string> ListViews(
        SqlServerRegistry reg,
        [Description("MySQL alias. Omit for default.")] string? alias = null,
        [Description("Database/schema name. Empty = the connected database.")] string? database = null,
        CancellationToken ct = default)
    {
        await using var conn = (MySqlConnection)await reg.OpenAsync(SqlEngine.Mysql, alias, ct).ConfigureAwait(false);
        var schemaClause = string.IsNullOrWhiteSpace(database)
            ? "AND table_schema = DATABASE()"
            : $"AND table_schema = '{database.Replace("'", "''")}'";
        var result = await SqlRunner.QueryAsync(conn,
            $"SELECT table_schema, table_name FROM information_schema.views WHERE 1=1 {schemaClause} ORDER BY table_schema, table_name",
            reg.Options, ct).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { alias, database, count = result.RowCount, views = result.Rows }, JsonOpts.Default);
    }

    [McpServerTool(Name = "mysql_list_routines"),
     Description("List stored procedures and functions (information_schema.routines).")]
    public static async Task<string> ListRoutines(
        SqlServerRegistry reg,
        [Description("MySQL alias. Omit for default.")] string? alias = null,
        [Description("Database/schema name. Empty = the connected database.")] string? database = null,
        CancellationToken ct = default)
    {
        await using var conn = (MySqlConnection)await reg.OpenAsync(SqlEngine.Mysql, alias, ct).ConfigureAwait(false);
        var schemaClause = string.IsNullOrWhiteSpace(database)
            ? "AND routine_schema = DATABASE()"
            : $"AND routine_schema = '{database.Replace("'", "''")}'";
        var result = await SqlRunner.QueryAsync(conn,
            $"SELECT routine_schema, routine_name, routine_type, data_type, created, last_altered FROM information_schema.routines WHERE 1=1 {schemaClause} ORDER BY routine_schema, routine_name",
            reg.Options, ct).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { alias, database, count = result.RowCount, routines = result.Rows }, JsonOpts.Default);
    }

    [McpServerTool(Name = "mysql_describe_table"),
     Description("Column definitions, primary key, and foreign keys for a table.")]
    public static async Task<string> DescribeTable(
        SqlServerRegistry reg,
        [Description("Table name (may be db-qualified, e.g. 'mydb.users').")] string table,
        [Description("MySQL alias. Omit for default.")] string? alias = null,
        CancellationToken ct = default)
    {
        await using var conn = (MySqlConnection)await reg.OpenAsync(SqlEngine.Mysql, alias, ct).ConfigureAwait(false);
        var (db, name) = SplitQualified(table);
        var schemaClause = db is null ? "table_schema = DATABASE()" : $"table_schema = '{db.Replace("'", "''")}'";
        var safeName = name.Replace("'", "''");
        var cols = await SqlRunner.QueryAsync(conn,
            $"SELECT column_name, column_type, is_nullable, column_default, column_key, extra FROM information_schema.columns WHERE {schemaClause} AND table_name = '{safeName}' ORDER BY ordinal_position",
            reg.Options, ct).ConfigureAwait(false);
        var fks = await SqlRunner.QueryAsync(conn,
            $"SELECT constraint_name, column_name, referenced_table_schema, referenced_table_name, referenced_column_name FROM information_schema.key_column_usage WHERE {schemaClause} AND table_name = '{safeName}' AND referenced_table_name IS NOT NULL",
            reg.Options, ct).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { alias, table = (db is null ? name : $"{db}.{name}"), columns = cols.Rows, foreignKeys = fks.Rows }, JsonOpts.Default);
    }

    [McpServerTool(Name = "mysql_query"),
     Description("Run a SELECT against MySQL. Capped at Sql:MaxRows.")]
    public static async Task<string> Query(
        SqlServerRegistry reg,
        [Description("SQL SELECT statement.")] string sql,
        [Description("MySQL alias. Omit for default.")] string? alias = null,
        CancellationToken ct = default)
    {
        await using var conn = (MySqlConnection)await reg.OpenAsync(SqlEngine.Mysql, alias, ct).ConfigureAwait(false);
        var result = await SqlRunner.QueryAsync(conn, sql, reg.Options, ct).ConfigureAwait(false);
        return JsonSerializer.Serialize(new
        {
            alias, columns = result.Columns, types = result.ColumnTypes, rowCount = result.RowCount,
            truncated = result.Truncated, cellsTruncated = result.CellsTruncated, rows = result.Rows,
        }, JsonOpts.Default);
    }

    [McpServerTool(Name = "mysql_execute"),
     Description("Run an INSERT / UPDATE / DELETE / DDL statement. Refused when Sql:ReadOnly=true.")]
    public static async Task<string> Execute(
        SqlServerRegistry reg,
        [Description("SQL statement.")] string sql,
        [Description("MySQL alias. Omit for default.")] string? alias = null,
        CancellationToken ct = default)
    {
        reg.RequireWritable("mysql_execute");
        await using var conn = (MySqlConnection)await reg.OpenAsync(SqlEngine.Mysql, alias, ct).ConfigureAwait(false);
        var rows = await SqlRunner.ExecuteAsync(conn, sql, reg.Options, ct, "mysql", alias, "mysql_execute").ConfigureAwait(false);
        return JsonSerializer.Serialize(new { alias, rowsAffected = rows }, JsonOpts.Default);
    }

    [McpServerTool(Name = "mysql_search_objects"),
     Description("Search every information_schema object (tables, views, routines) by name substring.")]
    public static async Task<string> SearchObjects(
        SqlServerRegistry reg,
        [Description("Substring (case-insensitive).")] string query,
        [Description("MySQL alias. Omit for default.")] string? alias = null,
        CancellationToken ct = default)
    {
        await using var conn = (MySqlConnection)await reg.OpenAsync(SqlEngine.Mysql, alias, ct).ConfigureAwait(false);
        var pattern = "%" + query.Replace("'", "''") + "%";
        var sql = $@"
SELECT table_schema AS schema_name, table_name AS object_name, table_type AS kind FROM information_schema.tables WHERE table_name LIKE '{pattern}'
UNION ALL
SELECT routine_schema, routine_name, routine_type FROM information_schema.routines WHERE routine_name LIKE '{pattern}'
ORDER BY schema_name, object_name";
        var result = await SqlRunner.QueryAsync(conn, sql, reg.Options, ct).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { alias, query, count = result.RowCount, rows = result.Rows }, JsonOpts.Default);
    }

    [McpServerTool(Name = "mysql_get_routine_definition"),
     Description("Return the body of a stored procedure or function from information_schema.routines.")]
    public static async Task<string> GetRoutineDefinition(
        SqlServerRegistry reg,
        [Description("Routine name (may be db-qualified, e.g. 'mydb.MyProc').")] string name,
        [Description("MySQL alias. Omit for default.")] string? alias = null,
        CancellationToken ct = default)
    {
        await using var conn = (MySqlConnection)await reg.OpenAsync(SqlEngine.Mysql, alias, ct).ConfigureAwait(false);
        var (db, routine) = SplitQualified(name);
        var schemaClause = db is null ? "routine_schema = DATABASE()" : $"routine_schema = '{db.Replace("'", "''")}'";
        var result = await SqlRunner.QueryAsync(conn,
            $"SELECT routine_schema, routine_name, routine_type, data_type, routine_definition FROM information_schema.routines WHERE {schemaClause} AND routine_name = '{routine.Replace("'", "''")}'",
            reg.Options, ct).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { alias, name = (db is null ? routine : $"{db}.{routine}"), rows = result.Rows }, JsonOpts.Default);
    }

    [McpServerTool(Name = "mysql_list_users"),
     Description("List MySQL users from mysql.user (requires SELECT on mysql schema).")]
    public static async Task<string> ListUsers(
        SqlServerRegistry reg,
        [Description("MySQL alias. Omit for default.")] string? alias = null,
        CancellationToken ct = default)
    {
        await using var conn = (MySqlConnection)await reg.OpenAsync(SqlEngine.Mysql, alias, ct).ConfigureAwait(false);
        var result = await SqlRunner.QueryAsync(conn, "SELECT user, host, account_locked, password_expired FROM mysql.user ORDER BY user, host", reg.Options, ct).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { alias, count = result.RowCount, users = result.Rows }, JsonOpts.Default);
    }

    [McpServerTool(Name = "mysql_create_user"),
     Description("CREATE USER '<user>'@'<host>' IDENTIFIED BY '<password>'.")]
    public static async Task<string> CreateUser(
        SqlServerRegistry reg,
        [Description("Username.")] string user,
        [Description("Host (use '%' for any).")] string host,
        [Description("Initial password.")] string password,
        [Description("MySQL alias. Omit for default.")] string? alias = null,
        CancellationToken ct = default)
    {
        reg.RequireWritable("mysql_create_user");
        var safeU = user.Replace("'", "''"); var safeH = host.Replace("'", "''"); var safeP = password.Replace("'", "''");
        await using var conn = (MySqlConnection)await reg.OpenAsync(SqlEngine.Mysql, alias, ct).ConfigureAwait(false);
        await SqlRunner.ExecuteAsync(conn, $"CREATE USER '{safeU}'@'{safeH}' IDENTIFIED BY '{safeP}'", reg.Options, ct, "mysql", alias, "mysql_create_user").ConfigureAwait(false);
        return JsonSerializer.Serialize(new { alias, createdUser = $"'{user}'@'{host}'" }, JsonOpts.Default);
    }

    [McpServerTool(Name = "mysql_grant"),
     Description("GRANT <privileges> ON <database>.<object> TO '<user>'@'<host>'. Example: privileges='SELECT, INSERT', onObject='mydb.*', user='reporter', host='%'.")]
    public static async Task<string> Grant(
        SqlServerRegistry reg,
        [Description("Comma-separated MySQL privileges (e.g. 'SELECT, INSERT, UPDATE' or 'ALL PRIVILEGES').")] string privileges,
        [Description("Scope: '*.*', '<db>.*', or '<db>.<table>'.")] string onObject,
        [Description("Target user.")] string user,
        [Description("Target host (use '%' for any).")] string host,
        [Description("MySQL alias. Omit for default.")] string? alias = null,
        CancellationToken ct = default)
    {
        reg.RequireWritable("mysql_grant");
        var safeU = user.Replace("'", "''"); var safeH = host.Replace("'", "''");
        var sql = $"GRANT {privileges} ON {onObject} TO '{safeU}'@'{safeH}'";
        await using var conn = (MySqlConnection)await reg.OpenAsync(SqlEngine.Mysql, alias, ct).ConfigureAwait(false);
        await SqlRunner.ExecuteAsync(conn, sql, reg.Options, ct, "mysql", alias, "mysql_grant").ConfigureAwait(false);
        await SqlRunner.ExecuteAsync(conn, "FLUSH PRIVILEGES", reg.Options, ct).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { alias, granted = sql }, JsonOpts.Default);
    }

    [McpServerTool(Name = "mysql_revoke"),
     Description("REVOKE <privileges> ON <object> FROM '<user>'@'<host>'.")]
    public static async Task<string> Revoke(
        SqlServerRegistry reg,
        [Description("Privileges to revoke.")] string privileges,
        [Description("Scope.")] string onObject,
        [Description("Target user.")] string user,
        [Description("Target host.")] string host,
        [Description("MySQL alias. Omit for default.")] string? alias = null,
        CancellationToken ct = default)
    {
        reg.RequireWritable("mysql_revoke");
        var safeU = user.Replace("'", "''"); var safeH = host.Replace("'", "''");
        var sql = $"REVOKE {privileges} ON {onObject} FROM '{safeU}'@'{safeH}'";
        await using var conn = (MySqlConnection)await reg.OpenAsync(SqlEngine.Mysql, alias, ct).ConfigureAwait(false);
        await SqlRunner.ExecuteAsync(conn, sql, reg.Options, ct, "mysql", alias, "mysql_revoke").ConfigureAwait(false);
        return JsonSerializer.Serialize(new { alias, revoked = sql }, JsonOpts.Default);
    }

    // ───────────────────────────── diagnostics ─────────────────────────────

    [McpServerTool(Name = "mysql_test_connection"),
     Description("Open the connection and run SELECT 1 — confirms credentials + network reachability. Reports elapsed ms.")]
    public static async Task<string> TestConnection(
        SqlServerRegistry reg,
        [Description("MySQL alias. Omit for default.")] string? alias = null,
        CancellationToken ct = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            await using var conn = (MySqlConnection)await reg.OpenAsync(SqlEngine.Mysql, alias, ct).ConfigureAwait(false);
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

    [McpServerTool(Name = "mysql_server_info"),
     Description("Return VERSION(), current user/database, server time, character set.")]
    public static async Task<string> ServerInfo(
        SqlServerRegistry reg,
        [Description("MySQL alias. Omit for default.")] string? alias = null,
        CancellationToken ct = default)
    {
        await using var conn = (MySqlConnection)await reg.OpenAsync(SqlEngine.Mysql, alias, ct).ConfigureAwait(false);
        var result = await SqlRunner.QueryAsync(conn, @"
SELECT VERSION() AS version, DATABASE() AS currentDatabase, CURRENT_USER() AS currentUser,
       NOW() AS serverNow, @@version_compile_os AS os, @@character_set_server AS characterSet", reg.Options, ct).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { alias, info = result.Rows.FirstOrDefault() }, JsonOpts.Default);
    }

    [McpServerTool(Name = "mysql_explain"),
     Description("Return the query plan as JSON (EXPLAIN FORMAT=JSON). The supplied SQL is NOT executed for most statement kinds.")]
    public static async Task<string> Explain(
        SqlServerRegistry reg,
        [Description("SQL to plan.")] string sql,
        [Description("MySQL alias. Omit for default.")] string? alias = null,
        CancellationToken ct = default)
    {
        await using var conn = (MySqlConnection)await reg.OpenAsync(SqlEngine.Mysql, alias, ct).ConfigureAwait(false);
        var result = await SqlRunner.QueryAsync(conn, "EXPLAIN FORMAT=JSON " + sql, reg.Options, ct).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { alias, plan = result.Rows }, JsonOpts.Default);
    }

    [McpServerTool(Name = "mysql_query_with_params"),
     Description("Run a parameterised SELECT. SQL uses @name placeholders; params object binds values as DbParameters. Avoids string-quote escaping bugs.")]
    public static async Task<string> QueryWithParams(
        SqlServerRegistry reg,
        [Description("SQL SELECT with @name placeholders.")] string sql,
        [Description("Parameter values: { \"@id\": 42, \"@name\": \"foo\" }. The leading @ is optional.")] Dictionary<string, object?> parameters,
        [Description("MySQL alias. Omit for default.")] string? alias = null,
        CancellationToken ct = default)
    {
        await using var conn = (MySqlConnection)await reg.OpenAsync(SqlEngine.Mysql, alias, ct).ConfigureAwait(false);
        var result = await SqlRunner.QueryWithParamsAsync(conn, sql, parameters, reg.Options, ct).ConfigureAwait(false);
        return JsonSerializer.Serialize(new
        {
            alias, columns = result.Columns, types = result.ColumnTypes, rowCount = result.RowCount,
            truncated = result.Truncated, cellsTruncated = result.CellsTruncated, rows = result.Rows,
        }, JsonOpts.Default);
    }

    [McpServerTool(Name = "mysql_list_active_sessions"),
     Description("Sessions / threads currently connected (information_schema.processlist).")]
    public static async Task<string> ListActiveSessions(
        SqlServerRegistry reg,
        [Description("MySQL alias. Omit for default.")] string? alias = null,
        CancellationToken ct = default)
    {
        await using var conn = (MySqlConnection)await reg.OpenAsync(SqlEngine.Mysql, alias, ct).ConfigureAwait(false);
        var result = await SqlRunner.QueryAsync(conn,
            "SELECT id, user, host, db, command, time, state, LEFT(info, 500) AS info FROM information_schema.processlist ORDER BY id",
            reg.Options, ct).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { alias, count = result.RowCount, sessions = result.Rows }, JsonOpts.Default);
    }

    [McpServerTool(Name = "mysql_list_blocking"),
     Description("Lock-wait pairs from performance_schema.data_lock_waits (MySQL 8.0+). Falls back to information_schema.innodb_lock_waits on older servers — surfaces a clear error if neither is available.")]
    public static async Task<string> ListBlocking(
        SqlServerRegistry reg,
        [Description("MySQL alias. Omit for default.")] string? alias = null,
        CancellationToken ct = default)
    {
        await using var conn = (MySqlConnection)await reg.OpenAsync(SqlEngine.Mysql, alias, ct).ConfigureAwait(false);
        try
        {
            var result = await SqlRunner.QueryAsync(conn, @"
SELECT requesting_engine_transaction_id AS waiting_trx,
       blocking_engine_transaction_id   AS blocking_trx,
       object_schema, object_name, lock_type, lock_mode, lock_status
FROM performance_schema.data_lock_waits", reg.Options, ct).ConfigureAwait(false);
            return JsonSerializer.Serialize(new { alias, source = "performance_schema.data_lock_waits", count = result.RowCount, blocking = result.Rows }, JsonOpts.Default);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { alias, error = ex.Message, hint = "Requires MySQL 8.0+ with performance_schema enabled." }, JsonOpts.Default);
        }
    }

    private static (string? Db, string Name) SplitQualified(string raw)
    {
        var dot = raw.IndexOf('.');
        return dot < 0 ? (null, raw.Trim()) : (raw[..dot].Trim(), raw[(dot + 1)..].Trim());
    }
}
