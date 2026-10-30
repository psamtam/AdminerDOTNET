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
}
