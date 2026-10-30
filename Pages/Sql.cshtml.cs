using AdminerDOTNET.Models;
using AdminerDOTNET.Services;
using Microsoft.AspNetCore.Mvc;

namespace AdminerDOTNET.Pages;

public class SqlModel(DatabaseConnection database, MetadataService metadata, QueryService queries) : DatabasePage(database, metadata)
{
    [BindProperty(SupportsGet=true)] public int? Id { get; set; }
    [BindProperty] public string CommandText { get; set; } = "";
    [BindProperty] public Guid ExecutionId { get; set; } = Guid.NewGuid();
    public QueryResult? Result { get; private set; }
    public async Task OnGetAsync()
    {
        SelectedId = Id;
        if (await LoadAsync() && Id.HasValue)
        {
            var obj = Objects.FirstOrDefault(o => o.Id == Id);
            if (obj is not null) CommandText = $"SELECT TOP (100) * FROM {obj.Qualified};";
        }
    }
    public async Task OnPostRunAsync()
    {
        SelectedId = Id;
        if (await LoadAsync())
        {
            Result = await queries.ExecuteAsync(ExecutionId, CommandText, Ct, Db);
            await LoadAsync();
        }
        ExecutionId = Guid.NewGuid();
        ModelState.Remove(nameof(ExecutionId));
    }
    public IActionResult OnPostCancel(Guid executionId)
    {
        try { return new JsonResult(new { cancelled = queries.Cancel(executionId, Db) }); }
        catch (Exception ex) when (Expected(ex)) { return BadRequest(new { error = Message(ex) }); }
    }
    public Task<IActionResult> OnPostExportAsync([FromServices] CsvExportStore store)
        => ExportAsync(queries, store, ExecutionId, CommandText);
    public async Task<IActionResult> OnGetDownloadAsync(string token, [FromServices] CsvExportStore store)
    {
        try
        {
            var selected = Connection.Resolve(Db, await Connection.DatabasesAsync(Ct));
            var stream = store.Open(token ?? "", Connection.ContextIdentity(selected));
            if (stream is null) return NotFound("This export is unavailable or expired. Execute & export CSV again.");
            return File(stream, "application/zip", "sql-results.zip");
        }
        catch (Exception ex) when (Expected(ex)) { return BadRequest(new { error = Message(ex) }); }
    }
    public async Task<IActionResult> OnGetCompletionsAsync()
    {
        try
        {
            var selected = Connection.Resolve(Db,await Connection.DatabasesAsync(Ct));
            return new JsonResult(await Metadata.CompletionsAsync(Ct,selected));
        }
        catch (Exception ex) when (Expected(ex)) { return BadRequest(new { error = Message(ex) }); }
    }
}
