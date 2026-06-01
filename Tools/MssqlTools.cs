using System.ComponentModel;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using ModelContextProtocol.Server;
using SQLMCPSharp.Services;

namespace SQLMCPSharp.Tools;

[McpServerToolType]
public sealed class MssqlTools
{
    // ───────────────────────────── discovery ─────────────────────────────

    [McpServerTool(Name = "mssql_list_servers"),
     Description("**CALL FIRST** when you don't already know the MSSQL aliases. Returns every configured connection's alias + description + the default alias picked when a tool's `alias` argument is omitted. Each alias is the handle every other mssql_* tool accepts.")]
    public static string ListServers(SqlServerRegistry reg)
    {
        reg.RequireEnabled(SqlEngine.Mssql);
        var aliases = reg.ListAliases(SqlEngine.Mssql);
        return JsonSerializer.Serialize(new
        {
            defaultAlias = reg.DefaultAlias(SqlEngine.Mssql),
            servers = aliases.Values.Select(s => new { alias = s.Alias, description = s.Description }),
        }, JsonOpts.Default);
    }

    [McpServerTool(Name = "mssql_list_databases"),
     Description("List databases on the MSSQL instance.")]
    public static async Task<string> ListDatabases(
        SqlServerRegistry reg,
        [Description("MSSQL alias. Omit for default.")] string? alias = null,
        CancellationToken ct = default)
    {
        await using var conn = (SqlConnection)await reg.OpenAsync(SqlEngine.Mssql, alias, ct).ConfigureAwait(false);
        var result = await SqlRunner.QueryAsync(conn, "SELECT name, database_id, create_date, state_desc, recovery_model_desc FROM sys.databases ORDER BY name", reg.Options, ct).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { alias, count = result.RowCount, databases = result.Rows }, JsonOpts.Default);
    }

    [McpServerTool(Name = "mssql_list_tables"),
     Description("List user tables. Optionally filter by schema. Returns schema, name, rowcount estimate, created/modified.")]
    public static async Task<string> ListTables(
        SqlServerRegistry reg,
        [Description("MSSQL alias. Omit for default.")] string? alias = null,
        [Description("Filter by schema (e.g. 'dbo'). Empty/null = all schemas.")] string? schema = null,
        CancellationToken ct = default)
    {
        await using var conn = (SqlConnection)await reg.OpenAsync(SqlEngine.Mssql, alias, ct).ConfigureAwait(false);
        var where = string.IsNullOrWhiteSpace(schema) ? "" : $"WHERE s.name = '{schema.Replace("'", "''")}'";
        var sql = $@"
SELECT s.name AS [schema], t.name AS [table], SUM(p.rows) AS [rowEstimate], t.create_date, t.modify_date
FROM sys.tables t
JOIN sys.schemas s ON s.schema_id = t.schema_id
LEFT JOIN sys.partitions p ON p.object_id = t.object_id AND p.index_id IN (0,1)
{where}
GROUP BY s.name, t.name, t.create_date, t.modify_date
ORDER BY s.name, t.name";
        var result = await SqlRunner.QueryAsync(conn, sql, reg.Options, ct).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { alias, schema, count = result.RowCount, tables = result.Rows }, JsonOpts.Default);
    }

    [McpServerTool(Name = "mssql_list_views"),
     Description("List user views.")]
    public static async Task<string> ListViews(
        SqlServerRegistry reg,
        [Description("MSSQL alias. Omit for default.")] string? alias = null,
        [Description("Filter by schema. Empty/null = all schemas.")] string? schema = null,
        CancellationToken ct = default)
    {
        await using var conn = (SqlConnection)await reg.OpenAsync(SqlEngine.Mssql, alias, ct).ConfigureAwait(false);
        var where = string.IsNullOrWhiteSpace(schema) ? "" : $"WHERE s.name = '{schema.Replace("'", "''")}'";
        var sql = $@"SELECT s.name AS [schema], v.name AS [view], v.create_date, v.modify_date
FROM sys.views v JOIN sys.schemas s ON s.schema_id = v.schema_id {where} ORDER BY s.name, v.name";
        var result = await SqlRunner.QueryAsync(conn, sql, reg.Options, ct).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { alias, schema, count = result.RowCount, views = result.Rows }, JsonOpts.Default);
    }

    [McpServerTool(Name = "mssql_list_stored_procedures"),
     Description("List stored procedures and scalar/table-valued functions.")]
    public static async Task<string> ListStoredProcedures(
        SqlServerRegistry reg,
        [Description("MSSQL alias. Omit for default.")] string? alias = null,
        [Description("Filter by schema. Empty/null = all schemas.")] string? schema = null,
        CancellationToken ct = default)
    {
        await using var conn = (SqlConnection)await reg.OpenAsync(SqlEngine.Mssql, alias, ct).ConfigureAwait(false);
        var where = string.IsNullOrWhiteSpace(schema) ? "" : $"AND s.name = '{schema.Replace("'", "''")}'";
        var sql = $@"SELECT s.name AS [schema], o.name AS [name], o.type_desc, o.create_date, o.modify_date
FROM sys.objects o JOIN sys.schemas s ON s.schema_id = o.schema_id
WHERE o.type IN ('P','PC','FN','IF','TF') {where} ORDER BY s.name, o.name";
        var result = await SqlRunner.QueryAsync(conn, sql, reg.Options, ct).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { alias, schema, count = result.RowCount, routines = result.Rows }, JsonOpts.Default);
    }

    [McpServerTool(Name = "mssql_describe_table"),
     Description("Columns, primary key, indexes, and foreign keys of a table.")]
    public static async Task<string> DescribeTable(
        SqlServerRegistry reg,
        [Description("Table name (may be schema-qualified, e.g. 'dbo.Users').")] string table,
        [Description("MSSQL alias. Omit for default.")] string? alias = null,
        CancellationToken ct = default)
    {
        await using var conn = (SqlConnection)await reg.OpenAsync(SqlEngine.Mssql, alias, ct).ConfigureAwait(false);
        var (schema, name) = SplitQualified(table, "dbo");
        var safeS = schema.Replace("'", "''"); var safeN = name.Replace("'", "''");
        var columns = await SqlRunner.QueryAsync(conn, $@"
SELECT c.name AS column_name, t.name AS data_type, c.max_length, c.precision, c.scale, c.is_nullable, c.is_identity,
       OBJECT_DEFINITION(c.default_object_id) AS default_definition
FROM sys.columns c
JOIN sys.types t ON t.user_type_id = c.user_type_id
JOIN sys.objects o ON o.object_id = c.object_id
JOIN sys.schemas s ON s.schema_id = o.schema_id
WHERE s.name = '{safeS}' AND o.name = '{safeN}'
ORDER BY c.column_id", reg.Options, ct).ConfigureAwait(false);
        var indexes = await SqlRunner.QueryAsync(conn, $@"
SELECT i.name AS index_name, i.type_desc, i.is_unique, i.is_primary_key,
       STUFF((SELECT ',' + c.name FROM sys.index_columns ic JOIN sys.columns c ON c.object_id=ic.object_id AND c.column_id=ic.column_id WHERE ic.object_id=i.object_id AND ic.index_id=i.index_id ORDER BY ic.key_ordinal FOR XML PATH('')),1,1,'') AS columns
FROM sys.indexes i
JOIN sys.objects o ON o.object_id = i.object_id
JOIN sys.schemas s ON s.schema_id = o.schema_id
WHERE s.name = '{safeS}' AND o.name = '{safeN}' AND i.type > 0", reg.Options, ct).ConfigureAwait(false);
        var fks = await SqlRunner.QueryAsync(conn, $@"
SELECT fk.name AS fk_name, OBJECT_SCHEMA_NAME(fk.referenced_object_id) AS referenced_schema, OBJECT_NAME(fk.referenced_object_id) AS referenced_table
FROM sys.foreign_keys fk
JOIN sys.objects o ON o.object_id = fk.parent_object_id
JOIN sys.schemas s ON s.schema_id = o.schema_id
WHERE s.name = '{safeS}' AND o.name = '{safeN}'", reg.Options, ct).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { alias, table = $"{schema}.{name}", columns = columns.Rows, indexes = indexes.Rows, foreignKeys = fks.Rows }, JsonOpts.Default);
    }

    [McpServerTool(Name = "mssql_search_objects"),
     Description("Search every user object (tables, views, procedures, functions, triggers) by name substring.")]
    public static async Task<string> SearchObjects(
        SqlServerRegistry reg,
        [Description("Substring (case-insensitive).")] string query,
        [Description("MSSQL alias. Omit for default.")] string? alias = null,
        CancellationToken ct = default)
    {
        await using var conn = (SqlConnection)await reg.OpenAsync(SqlEngine.Mssql, alias, ct).ConfigureAwait(false);
        var pattern = "%" + query.Replace("'", "''") + "%";
        var result = await SqlRunner.QueryAsync(conn, $@"
SELECT s.name AS [schema], o.name AS [name], o.type_desc, o.create_date, o.modify_date
FROM sys.objects o
JOIN sys.schemas s ON s.schema_id = o.schema_id
WHERE o.is_ms_shipped = 0 AND o.name LIKE '{pattern}'
ORDER BY o.type_desc, s.name, o.name", reg.Options, ct).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { alias, query, count = result.RowCount, rows = result.Rows }, JsonOpts.Default);
    }

    [McpServerTool(Name = "mssql_get_object_definition"),
     Description("Return the CREATE source of a view, stored procedure, function, or trigger via OBJECT_DEFINITION.")]
    public static async Task<string> GetObjectDefinition(
        SqlServerRegistry reg,
        [Description("Object name (may be schema-qualified, e.g. 'dbo.MyProc').")] string name,
        [Description("MSSQL alias. Omit for default.")] string? alias = null,
        CancellationToken ct = default)
    {
        await using var conn = (SqlConnection)await reg.OpenAsync(SqlEngine.Mssql, alias, ct).ConfigureAwait(false);
        var (schema, obj) = SplitQualified(name, "dbo");
        var safe = $"{schema}.{obj}".Replace("'", "''");
        var defs = await SqlRunner.SelectStringsAsync(conn, $"SELECT OBJECT_DEFINITION(OBJECT_ID('{safe}'))", reg.Options, ct).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { alias, name = $"{schema}.{obj}", definition = defs.FirstOrDefault() }, JsonOpts.Default);
    }

    // ───────────────────────────── query / execute ─────────────────────────────

    [McpServerTool(Name = "mssql_query"),
     Description("Run a SELECT (or any read-only T-SQL). Capped at Sql:MaxRows.")]
    public static async Task<string> Query(
        SqlServerRegistry reg,
        [Description("T-SQL SELECT statement.")] string sql,
        [Description("MSSQL alias. Omit for default.")] string? alias = null,
        CancellationToken ct = default)
    {
        await using var conn = (SqlConnection)await reg.OpenAsync(SqlEngine.Mssql, alias, ct).ConfigureAwait(false);
        var result = await SqlRunner.QueryAsync(conn, sql, reg.Options, ct).ConfigureAwait(false);
        return JsonSerializer.Serialize(new
        {
            alias, columns = result.Columns, types = result.ColumnTypes, rowCount = result.RowCount,
            truncated = result.Truncated, cellsTruncated = result.CellsTruncated, rows = result.Rows,
        }, JsonOpts.Default);
    }

    [McpServerTool(Name = "mssql_execute"),
     Description("Run an INSERT / UPDATE / DELETE / DDL statement. Refused when Sql:ReadOnly=true.")]
    public static async Task<string> Execute(
        SqlServerRegistry reg,
        [Description("T-SQL statement.")] string sql,
        [Description("MSSQL alias. Omit for default.")] string? alias = null,
        CancellationToken ct = default)
    {
        reg.RequireWritable("mssql_execute");
        await using var conn = (SqlConnection)await reg.OpenAsync(SqlEngine.Mssql, alias, ct).ConfigureAwait(false);
        var rows = await SqlRunner.ExecuteAsync(conn, sql, reg.Options, ct, "mssql", alias, "mssql_execute").ConfigureAwait(false);
        return JsonSerializer.Serialize(new { alias, rowsAffected = rows }, JsonOpts.Default);
    }

    // ───────────────────────────── editing: views / procedures ─────────────────────────────

    [McpServerTool(Name = "mssql_alter_view"),
     Description("Create or replace a view. The body must be the full SELECT (no CREATE VIEW prefix — we add CREATE OR ALTER for you).")]
    public static async Task<string> AlterView(
        SqlServerRegistry reg,
        [Description("View name (schema-qualified, e.g. 'dbo.MyView').")] string name,
        [Description("The SELECT statement the view should expose. Will be wrapped as: CREATE OR ALTER VIEW <name> AS <body>.")] string body,
        [Description("MSSQL alias. Omit for default.")] string? alias = null,
        CancellationToken ct = default)
    {
        reg.RequireWritable("mssql_alter_view");
        var (schema, view) = SplitQualified(name, "dbo");
        var sql = $"CREATE OR ALTER VIEW [{schema}].[{view}] AS\n{body}";
        await using var conn = (SqlConnection)await reg.OpenAsync(SqlEngine.Mssql, alias, ct).ConfigureAwait(false);
        await SqlRunner.ExecuteAsync(conn, sql, reg.Options, ct, "mssql", alias, "mssql_alter_view").ConfigureAwait(false);
        return JsonSerializer.Serialize(new { alias, view = $"{schema}.{view}" }, JsonOpts.Default);
    }

    [McpServerTool(Name = "mssql_alter_procedure"),
     Description("Create or replace a stored procedure. Pass the full CREATE OR ALTER PROCEDURE body verbatim (we don't wrap — this lets you control parameter declarations).")]
    public static async Task<string> AlterProcedure(
        SqlServerRegistry reg,
        [Description("Full T-SQL: e.g. 'CREATE OR ALTER PROCEDURE dbo.MyProc @p int AS BEGIN ... END'.")] string sql,
        [Description("MSSQL alias. Omit for default.")] string? alias = null,
        CancellationToken ct = default)
    {
        reg.RequireWritable("mssql_alter_procedure");
        await using var conn = (SqlConnection)await reg.OpenAsync(SqlEngine.Mssql, alias, ct).ConfigureAwait(false);
        await SqlRunner.ExecuteAsync(conn, sql, reg.Options, ct, "mssql", alias, "mssql_alter_procedure").ConfigureAwait(false);
        return JsonSerializer.Serialize(new { alias, executed = true }, JsonOpts.Default);
    }

    [McpServerTool(Name = "mssql_drop_object"),
     Description("DROP a table / view / procedure / function. Doubly gated: needs ReadOnly=false AND AllowDangerous=true.")]
    public static async Task<string> DropObject(
        SqlServerRegistry reg,
        [Description("Object type: TABLE | VIEW | PROCEDURE | FUNCTION | TRIGGER | INDEX.")] string objectType,
        [Description("Object name (schema-qualified for non-index).")] string name,
        [Description("MSSQL alias. Omit for default.")] string? alias = null,
        CancellationToken ct = default)
    {
        reg.RequireWritable("mssql_drop_object");
        reg.RequireDangerous("mssql_drop_object");
        var kind = objectType.Trim().ToUpperInvariant();
        var allowed = new[] { "TABLE", "VIEW", "PROCEDURE", "FUNCTION", "TRIGGER", "INDEX" };
        if (!allowed.Contains(kind))
            throw new ArgumentException($"objectType must be one of: {string.Join(", ", allowed)}", nameof(objectType));
        await using var conn = (SqlConnection)await reg.OpenAsync(SqlEngine.Mssql, alias, ct).ConfigureAwait(false);
        await SqlRunner.ExecuteAsync(conn, $"DROP {kind} {name}", reg.Options, ct, "mssql", alias, "mssql_drop_object").ConfigureAwait(false);
        return JsonSerializer.Serialize(new { alias, dropped = $"{kind} {name}" }, JsonOpts.Default);
    }

    // ───────────────────────────── users / permissions ─────────────────────────────

    [McpServerTool(Name = "mssql_list_logins"),
     Description("List server-level logins.")]
    public static async Task<string> ListLogins(
        SqlServerRegistry reg,
        [Description("MSSQL alias. Omit for default.")] string? alias = null,
        CancellationToken ct = default)
    {
        await using var conn = (SqlConnection)await reg.OpenAsync(SqlEngine.Mssql, alias, ct).ConfigureAwait(false);
        var result = await SqlRunner.QueryAsync(conn, "SELECT name, type_desc, is_disabled, create_date, modify_date FROM sys.server_principals WHERE type IN ('S','U','G') ORDER BY name", reg.Options, ct).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { alias, count = result.RowCount, logins = result.Rows }, JsonOpts.Default);
    }

    [McpServerTool(Name = "mssql_list_users"),
     Description("List database users in the currently connected database.")]
    public static async Task<string> ListUsers(
        SqlServerRegistry reg,
        [Description("MSSQL alias. Omit for default.")] string? alias = null,
        CancellationToken ct = default)
    {
        await using var conn = (SqlConnection)await reg.OpenAsync(SqlEngine.Mssql, alias, ct).ConfigureAwait(false);
        var result = await SqlRunner.QueryAsync(conn, "SELECT name, type_desc, default_schema_name, create_date, modify_date FROM sys.database_principals WHERE type IN ('S','U','G') AND name NOT LIKE '##%' ORDER BY name", reg.Options, ct).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { alias, count = result.RowCount, users = result.Rows }, JsonOpts.Default);
    }

    [McpServerTool(Name = "mssql_create_login"),
     Description("CREATE LOGIN <name> WITH PASSWORD = '...'. SQL-Server authentication only.")]
    public static async Task<string> CreateLogin(
        SqlServerRegistry reg,
        [Description("Login name (no spaces / SQL keywords).")] string name,
        [Description("Initial password (SQL-Auth). Required.")] string password,
        [Description("MSSQL alias. Omit for default.")] string? alias = null,
        CancellationToken ct = default)
    {
        reg.RequireWritable("mssql_create_login");
        var safeN = name.Replace("'", "''"); var safeP = password.Replace("'", "''");
        await using var conn = (SqlConnection)await reg.OpenAsync(SqlEngine.Mssql, alias, ct).ConfigureAwait(false);
        await SqlRunner.ExecuteAsync(conn, $"CREATE LOGIN [{safeN}] WITH PASSWORD = '{safeP}'", reg.Options, ct, "mssql", alias, "mssql_create_login").ConfigureAwait(false);
        return JsonSerializer.Serialize(new { alias, createdLogin = name }, JsonOpts.Default);
    }

    [McpServerTool(Name = "mssql_create_user"),
     Description("CREATE USER <user> FOR LOGIN <login> in the currently connected database.")]
    public static async Task<string> CreateUser(
        SqlServerRegistry reg,
        [Description("Database user name.")] string user,
        [Description("Existing server login to map to.")] string login,
        [Description("Optional default schema (e.g. 'dbo').")] string? defaultSchema = null,
        [Description("MSSQL alias. Omit for default.")] string? alias = null,
        CancellationToken ct = default)
    {
        reg.RequireWritable("mssql_create_user");
        var safeU = user.Replace("'", "''"); var safeL = login.Replace("'", "''");
        var schemaClause = string.IsNullOrWhiteSpace(defaultSchema) ? "" : $" WITH DEFAULT_SCHEMA = [{defaultSchema.Replace("'", "''")}]";
        await using var conn = (SqlConnection)await reg.OpenAsync(SqlEngine.Mssql, alias, ct).ConfigureAwait(false);
        await SqlRunner.ExecuteAsync(conn, $"CREATE USER [{safeU}] FOR LOGIN [{safeL}]{schemaClause}", reg.Options, ct, "mssql", alias, "mssql_create_user").ConfigureAwait(false);
        return JsonSerializer.Serialize(new { alias, createdUser = user, forLogin = login }, JsonOpts.Default);
    }

    [McpServerTool(Name = "mssql_grant"),
     Description("GRANT a permission on an object to a principal. Example: permission='SELECT', onObject='dbo.Users', toPrincipal='Reporting'.")]
    public static async Task<string> Grant(
        SqlServerRegistry reg,
        [Description("Permission verb (SELECT, INSERT, UPDATE, DELETE, EXECUTE, …).")] string permission,
        [Description("Securable object name (schema-qualified).")] string onObject,
        [Description("Grantee — user, role, or login.")] string toPrincipal,
        [Description("MSSQL alias. Omit for default.")] string? alias = null,
        CancellationToken ct = default)
    {
        reg.RequireWritable("mssql_grant");
        await using var conn = (SqlConnection)await reg.OpenAsync(SqlEngine.Mssql, alias, ct).ConfigureAwait(false);
        var sql = $"GRANT {permission} ON {onObject} TO [{toPrincipal.Replace("'", "''")}]";
        await SqlRunner.ExecuteAsync(conn, sql, reg.Options, ct, "mssql", alias, "mssql_grant").ConfigureAwait(false);
        return JsonSerializer.Serialize(new { alias, granted = sql }, JsonOpts.Default);
    }

    [McpServerTool(Name = "mssql_revoke"),
     Description("REVOKE a permission on an object from a principal.")]
    public static async Task<string> Revoke(
        SqlServerRegistry reg,
        [Description("Permission verb.")] string permission,
        [Description("Securable object name.")] string onObject,
        [Description("Principal to revoke from.")] string fromPrincipal,
        [Description("MSSQL alias. Omit for default.")] string? alias = null,
        CancellationToken ct = default)
    {
        reg.RequireWritable("mssql_revoke");
        await using var conn = (SqlConnection)await reg.OpenAsync(SqlEngine.Mssql, alias, ct).ConfigureAwait(false);
        var sql = $"REVOKE {permission} ON {onObject} FROM [{fromPrincipal.Replace("'", "''")}]";
        await SqlRunner.ExecuteAsync(conn, sql, reg.Options, ct, "mssql", alias, "mssql_revoke").ConfigureAwait(false);
        return JsonSerializer.Serialize(new { alias, revoked = sql }, JsonOpts.Default);
    }

    // ───────────────────────────── backup ─────────────────────────────

    [McpServerTool(Name = "mssql_backup_database"),
     Description("BACKUP DATABASE to a .bak file. Path is interpreted by the SQL Server engine (must be writeable by the SQL service account, not the agent's filesystem).")]
    public static async Task<string> BackupDatabase(
        SqlServerRegistry reg,
        [Description("Database name to back up.")] string database,
        [Description("Target file path on the SQL Server host.")] string toPath,
        [Description("If true, append to the backup set; otherwise INIT (overwrite). Default false.")] bool append = false,
        [Description("MSSQL alias. Omit for default.")] string? alias = null,
        CancellationToken ct = default)
    {
        reg.RequireWritable("mssql_backup_database");
        var safeDb = database.Replace("]", "]]"); var safePath = toPath.Replace("'", "''");
        var sql = $"BACKUP DATABASE [{safeDb}] TO DISK = N'{safePath}' WITH {(append ? "NOINIT" : "INIT")}, FORMAT, COMPRESSION";
        await using var conn = (SqlConnection)await reg.OpenAsync(SqlEngine.Mssql, alias, ct).ConfigureAwait(false);
        await SqlRunner.ExecuteAsync(conn, sql, reg.Options, ct, "mssql", alias, "mssql_backup_database").ConfigureAwait(false);
        return JsonSerializer.Serialize(new { alias, database, toPath, append }, JsonOpts.Default);
    }

    // ───────────────────────────── diagnostics ─────────────────────────────

    [McpServerTool(Name = "mssql_test_connection"),
     Description("Open the connection and run SELECT 1 — confirms credentials + network reachability. Reports elapsed ms.")]
    public static async Task<string> TestConnection(
        SqlServerRegistry reg,
        [Description("MSSQL alias. Omit for default.")] string? alias = null,
        CancellationToken ct = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            await using var conn = (SqlConnection)await reg.OpenAsync(SqlEngine.Mssql, alias, ct).ConfigureAwait(false);
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

    [McpServerTool(Name = "mssql_server_info"),
     Description("Return @@VERSION, edition, current user/database, server time, host info.")]
    public static async Task<string> ServerInfo(
        SqlServerRegistry reg,
        [Description("MSSQL alias. Omit for default.")] string? alias = null,
        CancellationToken ct = default)
    {
        await using var conn = (SqlConnection)await reg.OpenAsync(SqlEngine.Mssql, alias, ct).ConfigureAwait(false);
        var result = await SqlRunner.QueryAsync(conn, @"
SELECT
    @@VERSION                                   AS version,
    CAST(SERVERPROPERTY('Edition')   AS varchar(200)) AS edition,
    CAST(SERVERPROPERTY('ProductVersion') AS varchar(50)) AS productVersion,
    CAST(SERVERPROPERTY('MachineName') AS varchar(200)) AS machineName,
    DB_NAME()                                   AS currentDatabase,
    SUSER_SNAME()                               AS currentLogin,
    SUSER_NAME()                                AS currentUser,
    GETUTCDATE()                                AS serverUtc", reg.Options, ct).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { alias, info = result.Rows.FirstOrDefault() }, JsonOpts.Default);
    }

    [McpServerTool(Name = "mssql_explain"),
     Description("Return the estimated execution plan as XML (SHOWPLAN_XML). The supplied SQL is NOT executed.")]
    public static async Task<string> Explain(
        SqlServerRegistry reg,
        [Description("SQL to plan.")] string sql,
        [Description("MSSQL alias. Omit for default.")] string? alias = null,
        CancellationToken ct = default)
    {
        await using var conn = (SqlConnection)await reg.OpenAsync(SqlEngine.Mssql, alias, ct).ConfigureAwait(false);
        // SHOWPLAN_XML returns plans for every subsequent batch until turned off, as the
        // single message-stream result for each batch. Wrap so we always toggle it back off.
        await SqlRunner.ExecuteAsync(conn, "SET SHOWPLAN_XML ON", reg.Options, ct).ConfigureAwait(false);
        try
        {
            var result = await SqlRunner.QueryAsync(conn, sql, reg.Options, ct).ConfigureAwait(false);
            return JsonSerializer.Serialize(new { alias, plan = result.Rows }, JsonOpts.Default);
        }
        finally { await SqlRunner.ExecuteAsync(conn, "SET SHOWPLAN_XML OFF", reg.Options, ct).ConfigureAwait(false); }
    }

    [McpServerTool(Name = "mssql_query_with_params"),
     Description("Run a parameterised SELECT. SQL uses @name placeholders; params object binds values as DbParameters. Avoids string-quote escaping bugs.")]
    public static async Task<string> QueryWithParams(
        SqlServerRegistry reg,
        [Description("T-SQL SELECT with @name placeholders.")] string sql,
        [Description("Parameter values: { \"@id\": 42, \"@name\": \"foo\" }. The leading @ is optional.")] Dictionary<string, object?> parameters,
        [Description("MSSQL alias. Omit for default.")] string? alias = null,
        CancellationToken ct = default)
    {
        await using var conn = (SqlConnection)await reg.OpenAsync(SqlEngine.Mssql, alias, ct).ConfigureAwait(false);
        var result = await SqlRunner.QueryWithParamsAsync(conn, sql, parameters, reg.Options, ct).ConfigureAwait(false);
        return JsonSerializer.Serialize(new
        {
            alias, columns = result.Columns, types = result.ColumnTypes, rowCount = result.RowCount,
            truncated = result.Truncated, cellsTruncated = result.CellsTruncated, rows = result.Rows,
        }, JsonOpts.Default);
    }

    [McpServerTool(Name = "mssql_list_active_sessions"),
     Description("Sessions currently connected to the instance (sys.dm_exec_sessions joined with sys.dm_exec_requests).")]
    public static async Task<string> ListActiveSessions(
        SqlServerRegistry reg,
        [Description("MSSQL alias. Omit for default.")] string? alias = null,
        CancellationToken ct = default)
    {
        await using var conn = (SqlConnection)await reg.OpenAsync(SqlEngine.Mssql, alias, ct).ConfigureAwait(false);
        var result = await SqlRunner.QueryAsync(conn, @"
SELECT s.session_id, s.login_name, s.host_name, s.program_name, s.status, s.last_request_start_time,
       r.command, r.wait_type, r.blocking_session_id,
       DB_NAME(s.database_id) AS database_name
FROM sys.dm_exec_sessions s
LEFT JOIN sys.dm_exec_requests r ON r.session_id = s.session_id
WHERE s.is_user_process = 1
ORDER BY s.session_id", reg.Options, ct).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { alias, count = result.RowCount, sessions = result.Rows }, JsonOpts.Default);
    }

    [McpServerTool(Name = "mssql_list_blocking"),
     Description("Sessions that are currently blocking other sessions (sys.dm_exec_requests where blocking_session_id <> 0). Includes the SQL text the blocker is running.")]
    public static async Task<string> ListBlocking(
        SqlServerRegistry reg,
        [Description("MSSQL alias. Omit for default.")] string? alias = null,
        CancellationToken ct = default)
    {
        await using var conn = (SqlConnection)await reg.OpenAsync(SqlEngine.Mssql, alias, ct).ConfigureAwait(false);
        var result = await SqlRunner.QueryAsync(conn, @"
SELECT r.session_id AS blocked_session, r.blocking_session_id AS blocker_session,
       r.wait_type, r.wait_time, r.command, r.status,
       SUBSTRING(t.text, (r.statement_start_offset/2)+1,
                 (CASE r.statement_end_offset WHEN -1 THEN DATALENGTH(t.text) ELSE r.statement_end_offset END
                  - r.statement_start_offset)/2 + 1) AS blocked_sql_text
FROM sys.dm_exec_requests r
CROSS APPLY sys.dm_exec_sql_text(r.sql_handle) t
WHERE r.blocking_session_id <> 0", reg.Options, ct).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { alias, count = result.RowCount, blocking = result.Rows }, JsonOpts.Default);
    }

    // ───────────────────────────── helpers ─────────────────────────────

    private static (string Schema, string Name) SplitQualified(string raw, string defaultSchema)
    {
        var trimmed = raw.Trim();
        // Strip brackets from each half if present.
        trimmed = trimmed.Replace("[", "").Replace("]", "");
        var dot = trimmed.IndexOf('.');
        return dot < 0
            ? (defaultSchema, trimmed)
            : (trimmed[..dot], trimmed[(dot + 1)..]);
    }
}
