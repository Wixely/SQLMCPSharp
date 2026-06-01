using System.ComponentModel;
using System.Text;
using System.Text.Json;
using ModelContextProtocol.Server;
using Oracle.ManagedDataAccess.Client;
using SQLMCPSharp.Services;

namespace SQLMCPSharp.Tools;

[McpServerToolType]
public sealed class OracleTools
{
    [McpServerTool(Name = "oracle_list_servers"),
     Description("**CALL FIRST** when you don't already know the Oracle aliases. Returns every configured connection's alias + description + the default alias picked when a tool's `alias` argument is omitted. Each alias is the handle every other oracle_* tool accepts.")]
    public static string ListServers(SqlServerRegistry reg)
    {
        reg.RequireEnabled(SqlEngine.Oracle);
        var aliases = reg.ListAliases(SqlEngine.Oracle);
        return JsonSerializer.Serialize(new
        {
            defaultAlias = reg.DefaultAlias(SqlEngine.Oracle),
            servers = aliases.Values.Select(s => new { alias = s.Alias, description = s.Description }),
        }, JsonOpts.Default);
    }

    [McpServerTool(Name = "oracle_list_schemas"),
     Description("List schemas (users) visible to the connected account.")]
    public static async Task<string> ListSchemas(
        SqlServerRegistry reg,
        [Description("Oracle alias. Omit for default.")] string? alias = null,
        CancellationToken ct = default)
    {
        await using var conn = (OracleConnection)await reg.OpenAsync(SqlEngine.Oracle, alias, ct).ConfigureAwait(false);
        var result = await SqlRunner.QueryAsync(conn, "SELECT username, account_status, created FROM all_users ORDER BY username", reg.Options, ct).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { alias, count = result.RowCount, schemas = result.Rows }, JsonOpts.Default);
    }

    [McpServerTool(Name = "oracle_list_tables"),
     Description("List tables in a schema (defaults to USER_TABLES = the connected schema).")]
    public static async Task<string> ListTables(
        SqlServerRegistry reg,
        [Description("Oracle alias. Omit for default.")] string? alias = null,
        [Description("Owner/schema. Empty = current user (USER_TABLES).")] string? owner = null,
        CancellationToken ct = default)
    {
        await using var conn = (OracleConnection)await reg.OpenAsync(SqlEngine.Oracle, alias, ct).ConfigureAwait(false);
        var sql = string.IsNullOrWhiteSpace(owner)
            ? "SELECT table_name, num_rows, last_analyzed FROM user_tables ORDER BY table_name"
            : $"SELECT owner, table_name, num_rows, last_analyzed FROM all_tables WHERE owner = '{owner.ToUpperInvariant().Replace("'", "''")}' ORDER BY table_name";
        var result = await SqlRunner.QueryAsync(conn, sql, reg.Options, ct).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { alias, owner, count = result.RowCount, tables = result.Rows }, JsonOpts.Default);
    }

    [McpServerTool(Name = "oracle_list_views"),
     Description("List views in a schema.")]
    public static async Task<string> ListViews(
        SqlServerRegistry reg,
        [Description("Oracle alias. Omit for default.")] string? alias = null,
        [Description("Owner/schema. Empty = current user (USER_VIEWS).")] string? owner = null,
        CancellationToken ct = default)
    {
        await using var conn = (OracleConnection)await reg.OpenAsync(SqlEngine.Oracle, alias, ct).ConfigureAwait(false);
        var sql = string.IsNullOrWhiteSpace(owner)
            ? "SELECT view_name FROM user_views ORDER BY view_name"
            : $"SELECT owner, view_name FROM all_views WHERE owner = '{owner.ToUpperInvariant().Replace("'", "''")}' ORDER BY view_name";
        var result = await SqlRunner.QueryAsync(conn, sql, reg.Options, ct).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { alias, owner, count = result.RowCount, views = result.Rows }, JsonOpts.Default);
    }

    [McpServerTool(Name = "oracle_list_packages"),
     Description("List PL/SQL packages (and their bodies). Reads all_objects WHERE object_type IN ('PACKAGE','PACKAGE BODY').")]
    public static async Task<string> ListPackages(
        SqlServerRegistry reg,
        [Description("Oracle alias. Omit for default.")] string? alias = null,
        [Description("Owner/schema. Empty = current user.")] string? owner = null,
        CancellationToken ct = default)
    {
        await using var conn = (OracleConnection)await reg.OpenAsync(SqlEngine.Oracle, alias, ct).ConfigureAwait(false);
        var sql = string.IsNullOrWhiteSpace(owner)
            ? "SELECT object_name, object_type, status, last_ddl_time FROM user_objects WHERE object_type IN ('PACKAGE','PACKAGE BODY') ORDER BY object_name, object_type"
            : $"SELECT owner, object_name, object_type, status, last_ddl_time FROM all_objects WHERE object_type IN ('PACKAGE','PACKAGE BODY') AND owner = '{owner.ToUpperInvariant().Replace("'", "''")}' ORDER BY object_name, object_type";
        var result = await SqlRunner.QueryAsync(conn, sql, reg.Options, ct).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { alias, owner, count = result.RowCount, packages = result.Rows }, JsonOpts.Default);
    }

    [McpServerTool(Name = "oracle_get_package_source"),
     Description("Reassemble the PL/SQL source for a package or package body from ALL_SOURCE.")]
    public static async Task<string> GetPackageSource(
        SqlServerRegistry reg,
        [Description("Package name.")] string packageName,
        [Description("True = PACKAGE BODY, false = PACKAGE (the spec). Default false (spec).")] bool body = false,
        [Description("Owner/schema. Empty = current user.")] string? owner = null,
        [Description("Oracle alias. Omit for default.")] string? alias = null,
        CancellationToken ct = default)
    {
        await using var conn = (OracleConnection)await reg.OpenAsync(SqlEngine.Oracle, alias, ct).ConfigureAwait(false);
        var safeName = packageName.ToUpperInvariant().Replace("'", "''");
        var type = body ? "PACKAGE BODY" : "PACKAGE";
        var sql = string.IsNullOrWhiteSpace(owner)
            ? $"SELECT text FROM user_source WHERE name = '{safeName}' AND type = '{type}' ORDER BY line"
            : $"SELECT text FROM all_source WHERE owner = '{owner.ToUpperInvariant().Replace("'", "''")}' AND name = '{safeName}' AND type = '{type}' ORDER BY line";
        var lines = await SqlRunner.SelectStringsAsync(conn, sql, reg.Options, ct).ConfigureAwait(false);
        var source = new StringBuilder();
        foreach (var line in lines) source.Append(line);
        return JsonSerializer.Serialize(new { alias, owner, packageName, type, source = source.ToString() }, JsonOpts.Default);
    }

    [McpServerTool(Name = "oracle_alter_package_body"),
     Description("Compile a CREATE OR REPLACE PACKAGE BODY block. Pass the full statement verbatim (including 'CREATE OR REPLACE PACKAGE BODY <name> AS … END;').")]
    public static async Task<string> AlterPackageBody(
        SqlServerRegistry reg,
        [Description("Full PL/SQL: CREATE OR REPLACE PACKAGE BODY …")] string sql,
        [Description("Oracle alias. Omit for default.")] string? alias = null,
        CancellationToken ct = default)
    {
        reg.RequireWritable("oracle_alter_package_body");
        await using var conn = (OracleConnection)await reg.OpenAsync(SqlEngine.Oracle, alias, ct).ConfigureAwait(false);
        await SqlRunner.ExecuteAsync(conn, sql, reg.Options, ct, "oracle", alias, "oracle_alter_package_body").ConfigureAwait(false);
        return JsonSerializer.Serialize(new { alias, executed = true }, JsonOpts.Default);
    }

    [McpServerTool(Name = "oracle_describe_table"),
     Description("Columns, primary key, and foreign keys for a table.")]
    public static async Task<string> DescribeTable(
        SqlServerRegistry reg,
        [Description("Table name (may be owner-qualified, e.g. 'HR.EMPLOYEES').")] string table,
        [Description("Oracle alias. Omit for default.")] string? alias = null,
        CancellationToken ct = default)
    {
        await using var conn = (OracleConnection)await reg.OpenAsync(SqlEngine.Oracle, alias, ct).ConfigureAwait(false);
        var (owner, name) = SplitQualified(table);
        var ownerClause = owner is null ? "" : $"AND owner = '{owner.ToUpperInvariant().Replace("'", "''")}'";
        var view = owner is null ? "user_tab_columns" : "all_tab_columns";
        var safeName = name.ToUpperInvariant().Replace("'", "''");
        var cols = await SqlRunner.QueryAsync(conn,
            $"SELECT column_name, data_type, data_length, data_precision, data_scale, nullable, data_default FROM {view} WHERE table_name = '{safeName}' {ownerClause} ORDER BY column_id",
            reg.Options, ct).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { alias, table = (owner is null ? name : $"{owner}.{name}"), columns = cols.Rows }, JsonOpts.Default);
    }

    [McpServerTool(Name = "oracle_query"),
     Description("Run a SELECT against Oracle. Capped at Sql:MaxRows.")]
    public static async Task<string> Query(
        SqlServerRegistry reg,
        [Description("SQL SELECT statement.")] string sql,
        [Description("Oracle alias. Omit for default.")] string? alias = null,
        CancellationToken ct = default)
    {
        await using var conn = (OracleConnection)await reg.OpenAsync(SqlEngine.Oracle, alias, ct).ConfigureAwait(false);
        var result = await SqlRunner.QueryAsync(conn, sql, reg.Options, ct).ConfigureAwait(false);
        return JsonSerializer.Serialize(new
        {
            alias, columns = result.Columns, types = result.ColumnTypes, rowCount = result.RowCount,
            truncated = result.Truncated, cellsTruncated = result.CellsTruncated, rows = result.Rows,
        }, JsonOpts.Default);
    }

    [McpServerTool(Name = "oracle_execute"),
     Description("Run an INSERT / UPDATE / DELETE / DDL statement. Refused when Sql:ReadOnly=true.")]
    public static async Task<string> Execute(
        SqlServerRegistry reg,
        [Description("SQL statement.")] string sql,
        [Description("Oracle alias. Omit for default.")] string? alias = null,
        CancellationToken ct = default)
    {
        reg.RequireWritable("oracle_execute");
        await using var conn = (OracleConnection)await reg.OpenAsync(SqlEngine.Oracle, alias, ct).ConfigureAwait(false);
        var rows = await SqlRunner.ExecuteAsync(conn, sql, reg.Options, ct, "oracle", alias, "oracle_execute").ConfigureAwait(false);
        return JsonSerializer.Serialize(new { alias, rowsAffected = rows }, JsonOpts.Default);
    }

    [McpServerTool(Name = "oracle_search_objects"),
     Description("Search across tables, views, packages, procedures, functions, triggers by name substring.")]
    public static async Task<string> SearchObjects(
        SqlServerRegistry reg,
        [Description("Substring (case-insensitive).")] string query,
        [Description("Oracle alias. Omit for default.")] string? alias = null,
        [Description("Owner/schema. Empty = current user.")] string? owner = null,
        CancellationToken ct = default)
    {
        await using var conn = (OracleConnection)await reg.OpenAsync(SqlEngine.Oracle, alias, ct).ConfigureAwait(false);
        var pattern = "%" + query.ToUpperInvariant().Replace("'", "''") + "%";
        var sql = string.IsNullOrWhiteSpace(owner)
            ? $"SELECT object_type, object_name, status, last_ddl_time FROM user_objects WHERE object_name LIKE '{pattern}' ORDER BY object_type, object_name"
            : $"SELECT owner, object_type, object_name, status, last_ddl_time FROM all_objects WHERE owner = '{owner.ToUpperInvariant().Replace("'", "''")}' AND object_name LIKE '{pattern}' ORDER BY object_type, object_name";
        var result = await SqlRunner.QueryAsync(conn, sql, reg.Options, ct).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { alias, query, owner, count = result.RowCount, rows = result.Rows }, JsonOpts.Default);
    }

    [McpServerTool(Name = "oracle_get_user_privileges"),
     Description("Return system + object privileges granted to a user, plus role memberships.")]
    public static async Task<string> GetUserPrivileges(
        SqlServerRegistry reg,
        [Description("Username (UPPERCASE).")] string user,
        [Description("Oracle alias. Omit for default.")] string? alias = null,
        CancellationToken ct = default)
    {
        await using var conn = (OracleConnection)await reg.OpenAsync(SqlEngine.Oracle, alias, ct).ConfigureAwait(false);
        var u = user.ToUpperInvariant().Replace("'", "''");
        var sys = await SqlRunner.QueryAsync(conn, $"SELECT privilege, admin_option FROM dba_sys_privs WHERE grantee = '{u}' ORDER BY privilege", reg.Options, ct).ConfigureAwait(false);
        var tab = await SqlRunner.QueryAsync(conn, $"SELECT owner, table_name, privilege, grantable FROM dba_tab_privs WHERE grantee = '{u}' ORDER BY owner, table_name, privilege", reg.Options, ct).ConfigureAwait(false);
        var roles = await SqlRunner.QueryAsync(conn, $"SELECT granted_role, admin_option, default_role FROM dba_role_privs WHERE grantee = '{u}' ORDER BY granted_role", reg.Options, ct).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { alias, user, systemPrivileges = sys.Rows, tablePrivileges = tab.Rows, roles = roles.Rows }, JsonOpts.Default);
    }

    [McpServerTool(Name = "oracle_create_user"),
     Description("CREATE USER <name> IDENTIFIED BY <password>. Will also need GRANT CREATE SESSION (use oracle_grant).")]
    public static async Task<string> CreateUser(
        SqlServerRegistry reg,
        [Description("Username.")] string user,
        [Description("Initial password.")] string password,
        [Description("Optional default tablespace.")] string? defaultTablespace = null,
        [Description("Oracle alias. Omit for default.")] string? alias = null,
        CancellationToken ct = default)
    {
        reg.RequireWritable("oracle_create_user");
        var safeU = user.Replace("\"", ""); var safeP = password.Replace("'", "''");
        var ts = string.IsNullOrWhiteSpace(defaultTablespace) ? "" : $" DEFAULT TABLESPACE {defaultTablespace.Replace("\"", "")}";
        await using var conn = (OracleConnection)await reg.OpenAsync(SqlEngine.Oracle, alias, ct).ConfigureAwait(false);
        await SqlRunner.ExecuteAsync(conn, $"CREATE USER {safeU} IDENTIFIED BY \"{safeP}\"{ts}", reg.Options, ct, "oracle", alias, "oracle_create_user").ConfigureAwait(false);
        return JsonSerializer.Serialize(new { alias, createdUser = user }, JsonOpts.Default);
    }

    [McpServerTool(Name = "oracle_grant"),
     Description("GRANT a system privilege or object privilege. Example: privilege='SELECT', onObject='HR.EMPLOYEES', toUser='REPORTER'. Use privilege='CREATE SESSION', onObject='' for a system grant.")]
    public static async Task<string> Grant(
        SqlServerRegistry reg,
        [Description("Privilege (e.g. 'SELECT', 'CREATE SESSION').")] string privilege,
        [Description("Securable object name, OR empty string for system privileges.")] string onObject,
        [Description("Grantee username/role.")] string toUser,
        [Description("Oracle alias. Omit for default.")] string? alias = null,
        CancellationToken ct = default)
    {
        reg.RequireWritable("oracle_grant");
        await using var conn = (OracleConnection)await reg.OpenAsync(SqlEngine.Oracle, alias, ct).ConfigureAwait(false);
        var sql = string.IsNullOrWhiteSpace(onObject)
            ? $"GRANT {privilege} TO {toUser}"
            : $"GRANT {privilege} ON {onObject} TO {toUser}";
        await SqlRunner.ExecuteAsync(conn, sql, reg.Options, ct, "oracle", alias, "oracle_grant").ConfigureAwait(false);
        return JsonSerializer.Serialize(new { alias, granted = sql }, JsonOpts.Default);
    }

    // ───────────────────────────── diagnostics ─────────────────────────────

    [McpServerTool(Name = "oracle_test_connection"),
     Description("Open the connection and run SELECT 1 FROM DUAL — confirms credentials + listener reachability. Reports elapsed ms.")]
    public static async Task<string> TestConnection(
        SqlServerRegistry reg,
        [Description("Oracle alias. Omit for default.")] string? alias = null,
        CancellationToken ct = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            await using var conn = (OracleConnection)await reg.OpenAsync(SqlEngine.Oracle, alias, ct).ConfigureAwait(false);
            await SqlRunner.QueryAsync(conn, "SELECT 1 FROM DUAL", reg.Options, ct).ConfigureAwait(false);
            sw.Stop();
            return JsonSerializer.Serialize(new { alias, ok = true, elapsedMs = sw.ElapsedMilliseconds }, JsonOpts.Default);
        }
        catch (Exception ex)
        {
            sw.Stop();
            return JsonSerializer.Serialize(new { alias, ok = false, elapsedMs = sw.ElapsedMilliseconds, error = ex.Message }, JsonOpts.Default);
        }
    }

    [McpServerTool(Name = "oracle_server_info"),
     Description("Return v$version banner, current user, current schema/session info, server time, instance host.")]
    public static async Task<string> ServerInfo(
        SqlServerRegistry reg,
        [Description("Oracle alias. Omit for default.")] string? alias = null,
        CancellationToken ct = default)
    {
        await using var conn = (OracleConnection)await reg.OpenAsync(SqlEngine.Oracle, alias, ct).ConfigureAwait(false);
        var banner = (await SqlRunner.SelectStringsAsync(conn, "SELECT banner FROM v$version WHERE rownum = 1", reg.Options, ct).ConfigureAwait(false)).FirstOrDefault();
        var ctx = await SqlRunner.QueryAsync(conn, @"
SELECT SYS_CONTEXT('USERENV','SESSION_USER')    AS session_user,
       SYS_CONTEXT('USERENV','CURRENT_SCHEMA')  AS current_schema,
       SYS_CONTEXT('USERENV','DB_NAME')         AS db_name,
       SYS_CONTEXT('USERENV','SERVER_HOST')     AS server_host,
       SYS_CONTEXT('USERENV','INSTANCE_NAME')   AS instance_name,
       SYSTIMESTAMP                              AS server_time
FROM DUAL", reg.Options, ct).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { alias, banner, info = ctx.Rows.FirstOrDefault() }, JsonOpts.Default);
    }

    [McpServerTool(Name = "oracle_explain"),
     Description("Use EXPLAIN PLAN FOR + DBMS_XPLAN.DISPLAY to return the formatted execution plan. The supplied SQL is NOT executed.")]
    public static async Task<string> Explain(
        SqlServerRegistry reg,
        [Description("SQL to plan.")] string sql,
        [Description("Oracle alias. Omit for default.")] string? alias = null,
        CancellationToken ct = default)
    {
        await using var conn = (OracleConnection)await reg.OpenAsync(SqlEngine.Oracle, alias, ct).ConfigureAwait(false);
        // EXPLAIN PLAN writes into plan_table; DBMS_XPLAN.DISPLAY pivots it into rows of text.
        await SqlRunner.ExecuteAsync(conn, "EXPLAIN PLAN FOR " + sql, reg.Options, ct).ConfigureAwait(false);
        var lines = await SqlRunner.SelectStringsAsync(conn, "SELECT plan_table_output FROM table(DBMS_XPLAN.DISPLAY)", reg.Options, ct).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { alias, plan = lines }, JsonOpts.Default);
    }

    [McpServerTool(Name = "oracle_query_with_params"),
     Description("Run a parameterised SELECT. SQL uses :name placeholders (Oracle bind variables); params object binds values as DbParameters. Avoids string-quote escaping bugs.")]
    public static async Task<string> QueryWithParams(
        SqlServerRegistry reg,
        [Description("SQL SELECT with :name placeholders.")] string sql,
        [Description("Parameter values: { \"id\": 42, \"name\": \"foo\" }. Leading : is optional.")] Dictionary<string, object?> parameters,
        [Description("Oracle alias. Omit for default.")] string? alias = null,
        CancellationToken ct = default)
    {
        await using var conn = (OracleConnection)await reg.OpenAsync(SqlEngine.Oracle, alias, ct).ConfigureAwait(false);
        // OracleParameter requires names WITHOUT the leading colon — strip if present.
        var stripped = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (k, v) in parameters)
            stripped[k.TrimStart(':', '@')] = v;
        var result = await SqlRunner.QueryWithParamsAsync(conn, sql, stripped, reg.Options, ct).ConfigureAwait(false);
        return JsonSerializer.Serialize(new
        {
            alias, columns = result.Columns, types = result.ColumnTypes, rowCount = result.RowCount,
            truncated = result.Truncated, cellsTruncated = result.CellsTruncated, rows = result.Rows,
        }, JsonOpts.Default);
    }

    [McpServerTool(Name = "oracle_list_active_sessions"),
     Description("Active user sessions from v$session (excludes background processes).")]
    public static async Task<string> ListActiveSessions(
        SqlServerRegistry reg,
        [Description("Oracle alias. Omit for default.")] string? alias = null,
        CancellationToken ct = default)
    {
        await using var conn = (OracleConnection)await reg.OpenAsync(SqlEngine.Oracle, alias, ct).ConfigureAwait(false);
        var result = await SqlRunner.QueryAsync(conn, @"
SELECT sid, serial#, username, osuser, machine, program, status, sql_id, blocking_session, last_call_et, logon_time
FROM v$session
WHERE type <> 'BACKGROUND'
ORDER BY sid", reg.Options, ct).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { alias, count = result.RowCount, sessions = result.Rows }, JsonOpts.Default);
    }

    [McpServerTool(Name = "oracle_list_blocking"),
     Description("Sessions currently blocked by another session (v$session WHERE blocking_session IS NOT NULL).")]
    public static async Task<string> ListBlocking(
        SqlServerRegistry reg,
        [Description("Oracle alias. Omit for default.")] string? alias = null,
        CancellationToken ct = default)
    {
        await using var conn = (OracleConnection)await reg.OpenAsync(SqlEngine.Oracle, alias, ct).ConfigureAwait(false);
        var result = await SqlRunner.QueryAsync(conn, @"
SELECT s.sid, s.username, s.osuser, s.program, s.event,
       s.blocking_session, s.blocking_session_status,
       b.username AS blocker_username, b.program AS blocker_program, b.sql_id AS blocker_sql_id
FROM v$session s
LEFT JOIN v$session b ON b.sid = s.blocking_session
WHERE s.blocking_session IS NOT NULL", reg.Options, ct).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { alias, count = result.RowCount, blocking = result.Rows }, JsonOpts.Default);
    }

    private static (string? Owner, string Name) SplitQualified(string raw)
    {
        var dot = raw.IndexOf('.');
        return dot < 0 ? (null, raw.Trim()) : (raw[..dot].Trim(), raw[(dot + 1)..].Trim());
    }
}
