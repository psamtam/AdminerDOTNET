using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.RegularExpressions;
using AdminerDOTNET.Models;
using Microsoft.Data.SqlClient;

namespace AdminerDOTNET.Services;

public sealed class QueryService(DatabaseConnection database)
{
    private sealed record Execution(string Context, CancellationTokenSource Source);
    private readonly ConcurrentDictionary<Guid, Execution> running = new();
    public bool Cancel(Guid id, string? db = null)
    {
        if (!running.TryGetValue(id, out var execution) || execution.Context != database.ContextIdentity(db)) return false;
        try { execution.Source.Cancel(); return true; } catch (ObjectDisposedException) { return false; }
    }

    public async Task<QueryResult> ExecuteAsync(Guid id, string sql, CancellationToken request, string? db = null)
        => await RunAsync(id, sql, request, db);

    public async Task<ExportResult> ExportAsync(Guid id, string sql, CsvExportStore store, CancellationToken request, string? db = null)
    {
        var export = new ExportResult();
        var clock = Stopwatch.StartNew();
        try
        {
            await using var pending = store.Create();
            using var archive = new CsvArchive(pending.Stream);
            var result = await RunAsync(id, sql, request, db, archive, export, async ct =>
            {
                archive.Dispose();
                if (export.Sets.Count > 0) export.Token = await store.PublishAsync(pending, database.ContextIdentity(db), ct);
            });
            export.Error = result.Error;
            export.Affected = result.Affected;
            export.Messages.AddRange(result.Messages);
        }
        catch (OperationCanceledException) { export.Error = "Export cancelled. Completed statements may have committed."; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { export.Error = "Export could not be written. Check available temporary disk space and permissions. No partial download is available; completed statements may have committed."; }
        finally { export.Milliseconds = clock.Elapsed.TotalMilliseconds; }
        if (export.Error is not null)
        {
            if (export.Token is not null) store.Discard(export.Token);
            export.Token = null;
            if (!export.Error.Contains("committed", StringComparison.OrdinalIgnoreCase))
                export.Error += " No partial download is available; completed statements may have committed.";
        }
        return export;
    }

    private async Task<QueryResult> RunAsync(Guid id, string sql, CancellationToken request, string? db,
        CsvArchive? archive = null, ExportResult? export = null, Func<CancellationToken,Task>? complete = null)
    {
        var result = new QueryResult();
        if (string.IsNullOrWhiteSpace(sql)) { result.Error = "Enter a SQL batch to execute."; return result; }
        if (Regex.IsMatch(sql, @"^\s*GO(?:\s+\d+)?\s*(?:--.*)?$", RegexOptions.Multiline | RegexOptions.IgnoreCase))
        { result.Error = "GO is a client batch separator. Submit each batch separately without GO."; return result; }
        using var source = CancellationTokenSource.CreateLinkedTokenSource(request);
        if (!running.TryAdd(id, new(database.ContextIdentity(db), source))) { result.Error = "That execution is already running."; return result; }
        var clock = Stopwatch.StartNew();
        try
        {
            await using var connection = await database.OpenAsync(source.Token, db);
            connection.InfoMessage += (_, args) => { if (result.Messages.Count < 100) result.Messages.Add(args.Message); };
            using var command = new SqlCommand(sql, connection) { CommandTimeout = 60 };
            using var registration = source.Token.Register(() => { try { command.Cancel(); } catch { /* Connection may already be closing. */ } });
            using var reader = await command.ExecuteReaderAsync(source.Token);
            var cells = 0;
            var grids = 0;
            do
            {
                if (reader.FieldCount == 0) continue;
                if (archive is not null)
                {
                    export!.Sets.Add(await archive.WriteAsync(reader, export.Sets.Count + 1, source.Token));
                    continue;
                }
                var columns = Enumerable.Range(0, reader.FieldCount).Select(reader.GetName).ToList();
                var rows = new List<List<string?>>();
                var truncated = false;
                while (await reader.ReadAsync(source.Token))
                {
                    if (rows.Count >= 1000 || cells + reader.FieldCount > 10000 || grids >= 100)
                    { truncated = true; continue; }
                    rows.Add(Enumerable.Range(0, reader.FieldCount).Select(i => SqlValues.Read(reader, i)).ToList());
                    cells += reader.FieldCount;
                }
                if (grids++ < 100) result.Grids.Add(new(columns, rows, truncated));
                else if (!result.Messages.Contains("Only the first 100 result sets are displayed.")) result.Messages.Add("Only the first 100 result sets are displayed.");
            } while (await reader.NextResultAsync(source.Token));
            result.Affected = reader.RecordsAffected;
            source.Token.ThrowIfCancellationRequested();
            if (complete is not null) await complete(source.Token);
        }
        catch (OperationCanceledException) { result.Error = "Execution cancelled. Statements already completed may have committed; cancellation does not undo them."; }
        catch (SqlException ex) { result.Error = source.IsCancellationRequested ? "Execution cancelled. Completed statements may have committed." : ex.Number == -2 ? "Execution exceeded the 60-second command timeout. Completed statements may have committed." : $"SQL {ex.Number}, line {ex.LineNumber}: {ex.Message}"; }
        catch (InvalidOperationException ex) { result.Error = ex.Message; }
        catch (Exception ex) when (archive is not null && (ex is IOException or UnauthorizedAccessException))
        { result.Error = "Export could not be written. Check available temporary disk space and permissions. No partial download is available; completed statements may have committed."; }
        finally { running.TryRemove(id, out _); result.Milliseconds = clock.Elapsed.TotalMilliseconds; }
        return result;
    }
}
