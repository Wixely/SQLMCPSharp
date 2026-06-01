namespace SQLMCPSharp.Configuration;

/// <summary>
/// Top-level SQLMCPSharp config. One <see cref="EngineBundle"/> per supported DB engine.
/// Setting <c>Enabled=true</c> on a bundle unlocks its <c>&lt;engine&gt;_*</c> tools at runtime
/// (the tools are always registered with the MCP server; invocations throw a clear error
/// when the engine is disabled).
///
/// Each bundle can hold many connection entries with optional aliases. The alias is the
/// stable handle the agent uses to pick a target — e.g. <c>mssql_query(alias="UserDatabase", sql=...)</c>.
/// Missing aliases get auto-generated (<c>mssql-1</c>, <c>mssql-2</c>, …).
/// </summary>
public sealed class SqlOptions
{
    public const string SectionName = "Sql";

    /// <summary>
    /// Master safety switch. When true, every mutating tool (<c>*_execute</c>, <c>*_create_*</c>,
    /// <c>*_alter_*</c>, <c>*_grant</c>, …) is refused. Read tools (<c>*_query</c>, <c>*_list_*</c>,
    /// <c>*_describe_*</c>) stay available. Default true — flip explicitly per environment.
    /// </summary>
    public bool ReadOnly { get; set; } = true;

    /// <summary>
    /// Even with ReadOnly=false, tools that drop databases / users / objects are gated behind
    /// this second switch. Default false — drops require an explicit opt-in.
    /// </summary>
    public bool AllowDangerous { get; set; } = false;

    /// <summary>Per-query timeout in seconds. Applied via <c>DbCommand.CommandTimeout</c>.</summary>
    public int CommandTimeoutSeconds { get; set; } = 30;

    /// <summary>Max rows returned by <c>*_query</c>. Larger result sets are truncated with a flag.</summary>
    public int MaxRows { get; set; } = 1000;

    /// <summary>Max characters returned per cell before the tool truncates and flags. Keeps a single CLOB from blowing the agent's context.</summary>
    public int MaxCellChars { get; set; } = 8000;

    public EngineBundle Mssql { get; set; } = new();
    public EngineBundle Mysql { get; set; } = new();
    public EngineBundle Sqlite { get; set; } = new();
    public EngineBundle Oracle { get; set; } = new();
}

public sealed class EngineBundle
{
    /// <summary>Master enable for this engine. When false, every <c>&lt;engine&gt;_*</c> tool refuses with a clear error.</summary>
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// Alias of the connection picked when a tool omits its <c>alias</c> argument.
    /// Falls back to the first entry in <see cref="Servers"/> when blank.
    /// </summary>
    public string? DefaultAlias { get; set; }

    /// <summary>Configured connection entries for this engine.</summary>
    public List<SqlServerEntry> Servers { get; set; } = new();
}

public sealed class SqlServerEntry
{
    /// <summary>
    /// Stable handle the agent uses to pick this connection. When omitted, the server
    /// generates one at load time (<c>&lt;engine&gt;-N</c>).
    /// </summary>
    public string Alias { get; set; } = "";

    /// <summary>Provider-specific connection string (e.g. <c>Server=…;Database=…;Trusted_Connection=true;</c> for MSSQL).</summary>
    public string ConnectionString { get; set; } = "";

    /// <summary>Free-text description shown by <c>&lt;engine&gt;_list_servers</c>. Optional.</summary>
    public string Description { get; set; } = "";
}

public sealed class ServerOptions
{
    public const string SectionName = "Server";

    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 5712;
    public string Path { get; set; } = "/mcp";

    /// <summary>Service name when running as a Windows Service.</summary>
    public string WindowsServiceName { get; set; } = "SQLMCPSharp";

    /// <summary>Optional MCP endpoint password. Blank disables MCP password auth.</summary>
    public string Password { get; set; } = string.Empty;
}
