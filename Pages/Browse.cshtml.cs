using AdminerDOTNET.Models;
using AdminerDOTNET.Services;
using Microsoft.AspNetCore.Mvc;
using System.ComponentModel.DataAnnotations;

namespace AdminerDOTNET.Pages;

public class BrowseModel(DatabaseConnection database, MetadataService metadata, TableService tables, QueryService queries) : DatabasePage(database, metadata)
{
    [BindProperty(SupportsGet = true)] public int Id { get; set; }
    [BindProperty(SupportsGet = true)] public long P { get; set; } = 1;
    [BindProperty(SupportsGet = true)] public int? Sort { get; set; }
    [BindProperty(SupportsGet = true)] public bool Desc { get; set; }
    [BindProperty(SupportsGet = true), DisplayFormat(ConvertEmptyStringToNull = false)] public string? Filters { get; set; }
    [BindProperty(SupportsGet = true)] public int? Filter { get; set; }
    [BindProperty(SupportsGet = true)] public string? Op { get; set; } = "eq";
    [BindProperty(SupportsGet = true)] public string? Value { get; set; }
    [BindProperty] public string? CommandText { get; set; } = "";
    [BindProperty] public Guid ExecutionId { get; set; } = Guid.NewGuid();
    [BindProperty] public List<string> SelectedTokens { get; set; } = [];
    public BrowseResult? Result { get; private set; }
    public TableMetadata? Table { get; private set; }
    public QueryResult? Query { get; private set; }
    public bool CustomMode { get; private set; }
    public string PageLink(long p, int? sort = null, bool? desc = null) => Url.Page("/Browse", new { db=Db, id=Id, p, sort=sort??Sort, desc=desc??Desc, filters=Filters })!;
    public string ClearLink => Url.Page("/Browse", new { db=Db, id=Id, sort=Sort, desc=Desc })!;
    public string FilterColumns => System.Text.Json.JsonSerializer.Serialize(Table?.Columns.Select(c => new { id=c.Id, name=c.Name, type=c.DisplayType, operators=BrowseQuery.Operators(c) }), FilterState.Json);

    private FilterNode AppliedFilters()
    {
        var root = Filters is null && Filter.HasValue
            ? new FilterNode { Column=Filter, Operator=Op ?? "eq", Value=Value ?? "" }
            : FilterState.Parse(Filters);
        Filters = FilterState.Serialize(root);
        return root;
    }

    public async Task OnGetAsync()
    {
        SelectedId = Id;
        if (!await LoadAsync()) return;
        try
        {
            Table = await Metadata.GetAsync(Id, Ct, Db);
            if (!ModelState.IsValid) throw new InvalidOperationException("Invalid Browse values for: " + string.Join(", ",ModelState.Where(entry=>entry.Value?.Errors.Count>0).Select(entry=>entry.Key)) + ". Check the values and retry.");
            Result = await tables.BrowseAsync(Id, P, Sort, Desc, AppliedFilters(), Ct, Db);
            P = Result.Page;
            CommandText = Result.Sql;
            ModelState.Remove(nameof(P));
        }
        catch (Exception ex) when (Expected(ex)) { Error = Message(ex); }
    }

    public async Task OnPostRunAsync()
    {
        CustomMode = true;
        SelectedId = Id;
        if (await LoadAsync())
        {
            try
            {
                Table = await Metadata.GetAsync(Id, Ct, Db);
                if (!ModelState.IsValid) throw new InvalidOperationException("Invalid Browse state. Reset SQL and retry.");
                BrowseQuery.Build(Table, AppliedFilters(), Sort, Desc);
                Query = await queries.ExecuteAsync(ExecutionId, CommandText ?? "", Ct, Db);
                await LoadAsync();
            }
            catch (Exception ex) when (Expected(ex)) { Error = Message(ex); }
        }
        ExecutionId = Guid.NewGuid();
        ModelState.Remove(nameof(ExecutionId));
    }

    public async Task<IActionResult> OnPostDeleteSelectedAsync()
    {
        if (!await LoadAsync())
        {
            Error = "No selected rows were deleted. " + Error;
            return Page();
        }
        try
        {
            if (!ModelState.IsValid) throw new InvalidOperationException("Invalid selection or Browse state. Reload the table and retry.");
            Table = await Metadata.GetAsync(Id, Ct, Db);
            BrowseQuery.Build(Table, AppliedFilters(), Sort, Desc);
            var deleted = await tables.DeleteRowsAsync(Id, SelectedTokens, Ct, Db);
            TempData["Message"] = $"Deleted {deleted} selected row(s).";
        }
        catch (Exception ex) when (Expected(ex))
        {
            TempData["BulkDeleteError"] = "No selected rows were deleted. " + Message(ex);
        }
        return Redirect(PageLink(P));
    }
    public Task<IActionResult> OnPostExportAsync([FromServices] CsvExportStore store)
        => ExportAsync(queries, store, ExecutionId, CommandText ?? "");
}
