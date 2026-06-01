using System.Diagnostics;
using Serilog;

namespace SQLMCPSharp.Services;

/// <summary>
/// Audit logging for every mutating SQL operation. Wraps Serilog with a fixed
/// <c>SourceContext = "SqlAudit"</c> so operators can route audit lines to a separate
/// file sink via a Serilog filter (see README, "Audit log routing"). When no separate
/// sink is configured, audit lines still appear in the main log with the SqlAudit context.
///
/// Every tool that mutates state (<c>*_execute</c>, <c>*_alter_*</c>, <c>*_create_*</c>,
/// <c>*_drop_*</c>, <c>*_grant</c>, <c>*_revoke</c>, <c>*_backup_*</c>, <c>*_vacuum</c>)
/// MUST call <see cref="Record"/> after a successful run.
/// </summary>
public static class SqlAudit
{
    private static readonly ILogger Logger = Log.ForContext("SourceContext", "SqlAudit");

    /// <summary>
    /// Run the body under a stopwatch and emit a single audit line on completion. The
    /// stopwatch + structured logging keep one tool from forgetting to record. Re-throws
    /// any exception so the tool's normal error path still fires.
    /// </summary>
    public static async Task<T> RecordAsync<T>(
        string engine,
        string? alias,
        string operation,
        string sql,
        Func<Task<T>> body,
        int? rowsAffected = null)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var result = await body().ConfigureAwait(false);
            sw.Stop();
            Record(engine, alias, operation, sql, rowsAffected, sw.ElapsedMilliseconds, success: true, error: null);
            return result;
        }
        catch (Exception ex)
        {
            sw.Stop();
            Record(engine, alias, operation, sql, rowsAffected: null, sw.ElapsedMilliseconds, success: false, error: ex.Message);
            throw;
        }
    }

    public static void Record(
        string engine,
        string? alias,
        string operation,
        string sql,
        int? rowsAffected,
        long elapsedMs,
        bool success,
        string? error)
    {
        // Truncate SQL in the audit line so a massive DDL doesn't blow up the log file.
        // Operators wanting the full body can echo it via the calling tool's own logging.
        var clipped = sql.Length > 4000 ? sql[..4000] + "…(truncated)" : sql;
        Logger.Information(
            "SQL audit: engine={Engine} alias={Alias} op={Operation} success={Success} rows={Rows} elapsedMs={Elapsed} error={Error} sql={Sql}",
            engine, alias ?? "(default)", operation, success, rowsAffected, elapsedMs, error, clipped);
    }
}
