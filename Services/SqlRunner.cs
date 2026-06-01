using System.Data;
using System.Data.Common;
using SQLMCPSharp.Configuration;

namespace SQLMCPSharp.Services;

/// <summary>
/// Generic SELECT/EXECUTE runner used by every engine. Centralises three concerns:
/// row-count caps (MaxRows), cell-size caps (MaxCellChars), and command timeout. Keeps the
/// per-engine tool classes from each implementing their own DataReader loop.
/// </summary>
public static class SqlRunner
{
    /// <summary>Run a SELECT, return rows-as-dictionaries up to <paramref name="opts"/>.MaxRows.</summary>
    public static async Task<QueryResult> QueryAsync(
        DbConnection conn,
        string sql,
        SqlOptions opts,
        CancellationToken ct)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = Math.Max(1, opts.CommandTimeoutSeconds);

        using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var columns = new string[reader.FieldCount];
        var types = new string[reader.FieldCount];
        for (int i = 0; i < reader.FieldCount; i++)
        {
            columns[i] = reader.GetName(i);
            types[i] = reader.GetFieldType(i).Name;
        }

        var rows = new List<Dictionary<string, object?>>();
        var truncatedCells = 0;
        var capHit = false;
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            if (rows.Count >= opts.MaxRows) { capHit = true; break; }
            var row = new Dictionary<string, object?>(reader.FieldCount, StringComparer.Ordinal);
            for (int i = 0; i < reader.FieldCount; i++)
            {
                var raw = reader.IsDBNull(i) ? null : reader.GetValue(i);
                row[columns[i]] = ClampCell(raw, opts.MaxCellChars, ref truncatedCells);
            }
            rows.Add(row);
        }

        return new QueryResult
        {
            Columns = columns,
            ColumnTypes = types,
            Rows = rows,
            RowCount = rows.Count,
            Truncated = capHit,
            CellsTruncated = truncatedCells,
        };
    }

    /// <summary>
    /// Run a non-query SQL statement, return rows-affected. When <paramref name="auditOperation"/>
    /// is provided, the call also writes one SqlAudit line with engine/alias/operation/elapsed/rows.
    /// Internal helper calls (e.g. SET SHOWPLAN ON/OFF from explain) leave it null so they don't pollute the audit log.
    /// </summary>
    public static async Task<int> ExecuteAsync(
        DbConnection conn,
        string sql,
        SqlOptions opts,
        CancellationToken ct,
        string? auditEngine = null,
        string? auditAlias = null,
        string? auditOperation = null)
    {
        var sw = auditOperation is null ? null : System.Diagnostics.Stopwatch.StartNew();
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            cmd.CommandTimeout = Math.Max(1, opts.CommandTimeoutSeconds);
            var rows = await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            sw?.Stop();
            if (auditOperation is not null)
                SqlAudit.Record(auditEngine ?? "?", auditAlias, auditOperation, sql, rows, sw?.ElapsedMilliseconds ?? 0, success: true, error: null);
            return rows;
        }
        catch (Exception ex)
        {
            sw?.Stop();
            if (auditOperation is not null)
                SqlAudit.Record(auditEngine ?? "?", auditAlias, auditOperation, sql, rowsAffected: null, sw?.ElapsedMilliseconds ?? 0, success: false, error: ex.Message);
            throw;
        }
    }

    /// <summary>
    /// Parameterised query. Each entry in <paramref name="parameters"/> is bound as a
    /// <c>DbParameter</c>; the SQL references them using the engine's native placeholder
    /// (e.g. <c>@id</c> for MSSQL/MySQL/SQLite, <c>:id</c> for Oracle). Null values are
    /// converted to <see cref="DBNull"/>.
    /// </summary>
    public static async Task<QueryResult> QueryWithParamsAsync(
        DbConnection conn,
        string sql,
        IReadOnlyDictionary<string, object?> parameters,
        SqlOptions opts,
        CancellationToken ct)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = Math.Max(1, opts.CommandTimeoutSeconds);
        BindParameters(cmd, parameters);

        using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await ReadAllAsync(reader, opts, ct).ConfigureAwait(false);
    }

    /// <summary>Parameterised non-query — same parameter semantics as <see cref="QueryWithParamsAsync"/>. Audits when <paramref name="auditOperation"/> is non-null.</summary>
    public static async Task<int> ExecuteWithParamsAsync(
        DbConnection conn,
        string sql,
        IReadOnlyDictionary<string, object?> parameters,
        SqlOptions opts,
        CancellationToken ct,
        string? auditEngine = null,
        string? auditAlias = null,
        string? auditOperation = null)
    {
        var sw = auditOperation is null ? null : System.Diagnostics.Stopwatch.StartNew();
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            cmd.CommandTimeout = Math.Max(1, opts.CommandTimeoutSeconds);
            BindParameters(cmd, parameters);
            var rows = await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            sw?.Stop();
            if (auditOperation is not null)
                SqlAudit.Record(auditEngine ?? "?", auditAlias, auditOperation, sql, rows, sw?.ElapsedMilliseconds ?? 0, success: true, error: null);
            return rows;
        }
        catch (Exception ex)
        {
            sw?.Stop();
            if (auditOperation is not null)
                SqlAudit.Record(auditEngine ?? "?", auditAlias, auditOperation, sql, rowsAffected: null, sw?.ElapsedMilliseconds ?? 0, success: false, error: ex.Message);
            throw;
        }
    }

    private static void BindParameters(DbCommand cmd, IReadOnlyDictionary<string, object?> parameters)
    {
        foreach (var (name, value) in parameters)
        {
            var p = cmd.CreateParameter();
            p.ParameterName = name;
            p.Value = value ?? DBNull.Value;
            cmd.Parameters.Add(p);
        }
    }

    private static async Task<QueryResult> ReadAllAsync(DbDataReader reader, SqlOptions opts, CancellationToken ct)
    {
        var columns = new string[reader.FieldCount];
        var types = new string[reader.FieldCount];
        for (int i = 0; i < reader.FieldCount; i++)
        {
            columns[i] = reader.GetName(i);
            types[i] = reader.GetFieldType(i).Name;
        }

        var rows = new List<Dictionary<string, object?>>();
        var truncatedCells = 0;
        var capHit = false;
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            if (rows.Count >= opts.MaxRows) { capHit = true; break; }
            var row = new Dictionary<string, object?>(reader.FieldCount, StringComparer.Ordinal);
            for (int i = 0; i < reader.FieldCount; i++)
            {
                var raw = reader.IsDBNull(i) ? null : reader.GetValue(i);
                row[columns[i]] = ClampCell(raw, opts.MaxCellChars, ref truncatedCells);
            }
            rows.Add(row);
        }

        return new QueryResult
        {
            Columns = columns,
            ColumnTypes = types,
            Rows = rows,
            RowCount = rows.Count,
            Truncated = capHit,
            CellsTruncated = truncatedCells,
        };
    }

    /// <summary>Run a single-column SELECT and return the column as a string list (used for object-source lookups).</summary>
    public static async Task<List<string>> SelectStringsAsync(
        DbConnection conn,
        string sql,
        SqlOptions opts,
        CancellationToken ct)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = Math.Max(1, opts.CommandTimeoutSeconds);
        using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var list = new List<string>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            list.Add(reader.IsDBNull(0) ? "" : Convert.ToString(reader.GetValue(0)) ?? "");
        return list;
    }

    /// <summary>Stringify large CLOB-style cells with a marker. Other cell types pass through.</summary>
    private static object? ClampCell(object? value, int maxChars, ref int truncatedCount)
    {
        if (value is string s && s.Length > maxChars)
        {
            truncatedCount++;
            return s[..maxChars] + $"…(truncated, {s.Length} chars)";
        }
        if (value is byte[] b && b.Length > maxChars)
        {
            truncatedCount++;
            return $"<binary {b.Length} bytes — truncated>";
        }
        return value;
    }
}

public sealed class QueryResult
{
    public required string[] Columns { get; init; }
    public required string[] ColumnTypes { get; init; }
    public required List<Dictionary<string, object?>> Rows { get; init; }
    public int RowCount { get; init; }
    public bool Truncated { get; init; }
    public int CellsTruncated { get; init; }
}
