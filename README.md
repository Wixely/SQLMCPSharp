# SQLMCPSharp

A Model Context Protocol (MCP) server that lets AI agents browse and edit relational
databases across **Microsoft SQL Server**, **MySQL / MariaDB**, **SQLite**, and **Oracle**.
Built on the MCPSharp product line.

## What an agent can do

For every engine that's `Enabled: true` in config, the agent gets a matching set of
`<engine>_*` tools — same shape across engines:

- `<engine>_list_servers` — show configured aliases (e.g. `UserDatabase`, `ProdOracle`)
- `<engine>_list_databases` / `_list_schemas` (Oracle)
- `<engine>_list_tables` / `_list_views`
- `<engine>_describe_table`
- `<engine>_search_objects` — substring search across tables, views, routines
- `<engine>_query` — read-only SELECT, capped at `Sql:MaxRows` and `Sql:MaxCellChars`
- `<engine>_execute` — INSERT / UPDATE / DELETE / DDL (gated by `Sql:ReadOnly=false`)

Plus engine-specific extras:

- **MSSQL** (16 tools): `list_stored_procedures`, `get_object_definition`, `alter_view`,
  `alter_procedure`, `drop_object` (double-gated), `list_logins`, `list_users`,
  `create_login`, `create_user`, `grant`, `revoke`, `backup_database`
- **MySQL** (13 tools): `list_routines`, `get_routine_definition`, `list_users`,
  `create_user`, `grant`, `revoke`
- **SQLite** (9 tools): `vacuum`, `backup` (online backup API), `get_definition`
- **Oracle** (12 tools): `list_packages`, `get_package_source`, `alter_package_body`,
  `get_user_privileges`, `create_user`, `grant`

### Diagnostics (every engine)

Each engine also exposes:

- `<engine>_test_connection` — opens the connection, runs `SELECT 1` (or `SELECT 1 FROM DUAL` for Oracle), reports `ok` + `elapsedMs`. Fast first move when debugging credentials or networking.
- `<engine>_server_info` — version / edition / current user / current database / server time.
- `<engine>_explain` — engine-native plan: SQLite `EXPLAIN QUERY PLAN`, MSSQL `SHOWPLAN_XML`, MySQL `EXPLAIN FORMAT=JSON`, Oracle `EXPLAIN PLAN FOR` + `DBMS_XPLAN.DISPLAY`. Plan only — the supplied SQL is NOT executed.
- `<engine>_query_with_params` — parameterised SELECT (`@name` for MSSQL/MySQL/SQLite, `:name` for Oracle). Values bind as `DbParameter` instead of being concatenated — no quote-escaping bugs and binary / dates / nulls round-trip cleanly.
- `<engine>_list_active_sessions` — MSSQL `sys.dm_exec_sessions`, MySQL `information_schema.processlist`, Oracle `v$session WHERE type<>'BACKGROUND'`. (SQLite has no concept of sessions, so it's intentionally absent.)
- `<engine>_list_blocking` — MSSQL `sys.dm_exec_requests WHERE blocking_session_id<>0`, MySQL `performance_schema.data_lock_waits` (8.0+), Oracle `v$session WHERE blocking_session IS NOT NULL`. (SQLite single-writer — absent.)

## Aliases

Every engine block in the config takes a list of connections, each with an optional
`Alias`. Aliases are the handle an agent passes to a tool:

```json
"Mssql": {
  "Enabled": true,
  "DefaultAlias": "UserDatabase",
  "Servers": [
    { "Alias": "UserDatabase",   "ConnectionString": "Server=…;Database=Users;…" },
    { "Alias": "ReportingMart",  "ConnectionString": "Server=…;Database=DW;…", "Description": "Read replica for reports" }
  ]
}
```

Servers without an explicit `Alias` get auto-named `<engine>-1`, `<engine>-2`, … so the
agent always has something to refer to. The default alias is the first entry unless
`DefaultAlias` says otherwise.

## MSSQL configuration example

A complete `Sql.Mssql` block with the connection-string variants you're most likely to need
in `SQLMCPSharp.json`:

```jsonc
"Mssql": {
  "Enabled": true,
  "DefaultAlias": "UserDatabase",
  "Servers": [
    {
      "Alias": "UserDatabase",
      "Description": "Production user store — Windows auth",
      "ConnectionString": "Server=sql01.corp.local;Database=Users;Trusted_Connection=True;Encrypt=True;TrustServerCertificate=True;Application Name=SQLMCPSharp"
    },
    {
      "Alias": "ReportingMart",
      "Description": "Read-replica with a service account",
      "ConnectionString": "Server=tcp:reporting.corp.local,1433;Database=DW;User Id=mcp_reader;Password=REPLACE_ME;Encrypt=True;TrustServerCertificate=True;Application Name=SQLMCPSharp"
    },
    {
      "Alias": "LocalDev",
      "Description": "SQL Server Express on this box",
      "ConnectionString": "Server=(local)\\SQLEXPRESS;Database=master;Trusted_Connection=True;Encrypt=False;Application Name=SQLMCPSharp"
    },
    {
      "Alias": "Azure",
      "Description": "Azure SQL with an AAD-Password identity",
      "ConnectionString": "Server=tcp:myserver.database.windows.net,1433;Database=mydb;Authentication=Active Directory Password;User Id=dba@corp.local;Password=REPLACE_ME;Encrypt=True"
    }
  ]
}
```

### Per-entry field notes
| Field | What it does |
| --- | --- |
| `Alias` | Handle the agent passes as the `alias` argument (e.g. `mssql_query(alias="UserDatabase", …)`). Omit and you'll get `mssql-1`, `mssql-2`, …. |
| `Description` | Surfaced by `mssql_list_servers` — helps the model pick the right target. |
| `ConnectionString` | Standard `Microsoft.Data.SqlClient` connection string. |

### Connection-string keys worth knowing
| Key | Effect |
| --- | --- |
| `Server=host\Instance,port` | Hostname (+ optional named instance / TCP port). Prefix `tcp:` to force TCP transport. |
| `Database=…` | Initial catalog. `mssql_list_databases` works from any DB; tools like `mssql_list_users` and `mssql_create_user` act against this DB unless you change it. |
| `Trusted_Connection=True` | Windows / Kerberos auth (no User Id / Password). |
| `User Id=…;Password=…` | SQL Server authentication. |
| `Authentication=Active Directory …` | Azure SQL identities: `Active Directory Password` (login + password), `Active Directory Integrated` (Windows SSO), `Active Directory Managed Identity` (VM/App Service). |
| `Encrypt=True;TrustServerCertificate=True` | Microsoft.Data.SqlClient 4.x+ defaults `Encrypt=True`. On-prem with self-signed certs needs `TrustServerCertificate=True` to skip cert chain validation. |
| `Application Name=SQLMCPSharp` | Shows up in `sys.dm_exec_sessions.program_name` — handy for tracing the agent's activity in prod. |

### Permissions the connection account needs
- **Read tools** (`mssql_query`, `_describe_table`, `_list_*`, `_search_objects`): just `SELECT` on the target objects, plus `VIEW DEFINITION` for `mssql_get_object_definition`.
- **`mssql_alter_view` / `mssql_alter_procedure`**: `ALTER` on the object (or `db_ddladmin`).
- **`mssql_list_logins` / `mssql_create_login`**: `VIEW ANY DEFINITION` and `ALTER ANY LOGIN` at the server scope (or `sysadmin`).
- **`mssql_create_user`**: `ALTER ANY USER` (or `db_accessadmin`) on the target database.
- **`mssql_grant` / `mssql_revoke`**: `WITH GRANT OPTION` on the object, or `db_securityadmin` role membership.
- **`mssql_backup_database`**: `db_backupoperator` on the source database, and the SQL Server service account needs write access to `toPath`.
- **`mssql_drop_object`** (doubly-gated): same `ALTER` rights as the corresponding `_alter_*` tool — but only fires when both `Sql:ReadOnly=false` and `Sql:AllowDangerous=true`.

After saving the config, restart the server. `/healthz` should flip MSSQL to
`enabled:true, count:<N>`, and `mssql_list_servers` will return every alias.

## Configuration

`SQLMCPSharp.json` next to the executable. Override per-key via `SQLMCP_` env vars
(e.g. `SQLMCP_Sql__ReadOnly=false`, `SQLMCP_Sql__Mssql__Enabled=true`).

| Section / key | Purpose |
| --- | --- |
| `Sql.ReadOnly` | Master safety: block every write/DDL/grant tool. **Default true.** |
| `Sql.AllowDangerous` | Second gate for destructive drops. **Default false.** |
| `Sql.CommandTimeoutSeconds` | Per-statement timeout. Default 30. |
| `Sql.MaxRows` | Cap rows returned by `*_query`. Default 1000. |
| `Sql.MaxCellChars` | Truncate single cells past this length. Default 8000. |
| `Sql.<Engine>.Enabled` | Master per-engine switch. **Default false.** |
| `Sql.<Engine>.DefaultAlias` | Which alias is used when a tool omits `alias`. |
| `Sql.<Engine>.Servers` | Array of `{ Alias, ConnectionString, Description }`. |
| `Server.Host` / `Port` / `Path` | HTTP listener. Default `localhost:5712/mcp`. |
| `Server.Password` | Optional MCP endpoint password. Blank = open. |

## Endpoints

- `GET /healthz` — readiness + per-engine status
- MCP HTTP transport at `Server.Path` (default `/mcp`)

## Safety posture

- `Sql:ReadOnly=true` blocks every `*_execute`, `*_alter_*`, `*_create_*`, `*_grant`,
  `*_revoke`, `*_backup_*`, `*_drop_*`, `*_vacuum` tool.
- `Sql:AllowDangerous=false` (default) additionally blocks `mssql_drop_object` even when
  `ReadOnly=false`.
- Each engine is disabled by default — only the engines you turn on register tools that work.

## NuGet packages and licensing

The user asked for MIT licensing where possible:

| Engine | NuGet package | License |
| --- | --- | --- |
| MSSQL | `Microsoft.Data.SqlClient` | **MIT** |
| MySQL | `MySqlConnector` | **MIT** (NB: not `MySql.Data`, which is GPL) |
| SQLite | `Microsoft.Data.Sqlite` | **MIT** |
| Oracle | `Oracle.ManagedDataAccess.Core` | **Oracle Free Use Terms and Conditions** — *not* MIT, but free for use and redistribution. No MIT-licensed .NET Oracle driver exists. If this is a blocker, set `Sql:Oracle:Enabled=false` and the package's bytes are still on disk but no tools surface. |

## Running

```pwsh
dotnet run --project SQLMCPSharp -c Release
```

Or publish single-file and run the produced `SQLMCPSharp.exe`. JSON config is read from
next to the executable so a single-file deployment keeps tunables external.

## Audit log routing

Every mutating tool (`*_execute`, `*_alter_*`, `*_create_*`, `*_drop_*`, `*_grant`,
`*_revoke`, `*_backup_*`, `*_vacuum`) emits one structured log line via Serilog:

```
SQL audit: engine=mssql alias=UserDatabase op=mssql_alter_view success=true rows=-1
           elapsedMs=42 error= sql=CREATE OR ALTER VIEW [dbo].[ActiveUsers] AS …
```

The line is tagged `SourceContext = "SqlAudit"`. By default it flows into the main
`logs/sqlmcp-*.log` file alongside every other Serilog message — `grep "SQL audit"` is
the path of least resistance.

To route audit lines to a **separate file**, add the `Serilog.Expressions` package to the
deployment and extend `Serilog.WriteTo`:

```jsonc
{
  "Name": "Logger",
  "Args": {
    "configureLogger": {
      "Filter": [
        { "Name": "ByIncludingOnly", "Args": { "expression": "SourceContext = 'SqlAudit'" } }
      ],
      "WriteTo": [
        { "Name": "File", "Args": {
            "path": "logs/sqlmcp-audit-.log",
            "rollingInterval": "Day",
            "retainedFileCountLimit": 365,
            "shared": true,
            "outputTemplate": "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} {Message:lj}{NewLine}"
        }}
      ]
    }
  }
}
```

Pairs nicely with a long `retainedFileCountLimit` on the audit file (operators usually
want years of mutation history, not days) and a tighter retention on the bulk app log.

If you don't want the audit log polluting the regular file, layer a `ByExcluding` filter
on the existing `File` sink:
`"Filter": [{ "Name": "ByExcluding", "Args": { "expression": "SourceContext = 'SqlAudit'" }}]`.

## A note on injection

The tools accept SQL strings from the agent and run them. SQL injection isn't a concern
in the traditional sense (the "user" *is* the SQL author), but accidental DROPs / GRANTs
absolutely are — that's what `Sql:ReadOnly` and `Sql:AllowDangerous` guard against.
Object-name parameters (e.g. `mssql_alter_view name="dbo.MyView"`) are escaped before
being interpolated into DDL, but agents should still pass them as you'd type them yourself,
not as concatenated user input.
