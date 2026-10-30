using AdminerDOTNET.Models;
using AdminerDOTNET.Services;
using Microsoft.AspNetCore.Mvc;

namespace AdminerDOTNET.Pages;

public class RecordModel(DatabaseConnection database, MetadataService metadata, TableService tables) : DatabasePage(database, metadata)
{
    [BindProperty(SupportsGet=true)] public int Id { get; set; }
    [BindProperty] public string? Token { get; set; }
    [BindProperty] public Dictionary<int,string> Modes { get; set; } = [];
    [BindProperty] public Dictionary<int,string?> Values { get; set; } = [];
    public TableMetadata? Table { get; private set; }
    public RowSnapshot? Snapshot { get; private set; }
    public bool Ready => Table is not null && (Token is null ? Table.CanInsert : Table.CanEdit && Snapshot is not null);
    private async Task PrepareAsync(bool populate)
    {
        SelectedId = Id;
        if (!await LoadAsync()) return;
        try
        {
            Table = await Metadata.GetAsync(Id, Ct, Db);
            if (Token is null ? !Table.CanInsert : !Table.CanEdit) throw new InvalidOperationException("This object does not support this record operation. Use SQL for views or unsupported matching types.");
            if (Token is not null) Snapshot = tables.Decode(Table, Token, Db);
            if (populate)
            {
                foreach (var column in Table.Columns)
                {
                    var original = Snapshot?.Values[Table.Columns.IndexOf(column)];
                    Modes[column.Id] = Snapshot is null ? "default" : original is null ? "null" : "value";
                    Values[column.Id] = original;
                }
            }
        }
        catch (Exception ex) when (Expected(ex)) { Error = Message(ex); }
    }
    public async Task OnGetAsync() => await PrepareAsync(true);
    public async Task OnPostOpenAsync() => await PrepareAsync(true);
    public async Task OnPostReloadAsync()
    {
        await PrepareAsync(false);
        if (!Ready || Error is not null || Token is null) return;
        try
        {
            Token = await tables.RefreshAsync(Id, Token, Ct, Db);
            ModelState.Clear();
            await PrepareAsync(true);
        }
        catch (Exception ex) when (Expected(ex)) { Error = Message(ex); }
    }
    public async Task<IActionResult> OnPostSaveAsync() => await MutateAsync(false);
    public async Task<IActionResult> OnPostDeleteAsync() => await MutateAsync(true);
    private async Task<IActionResult> MutateAsync(bool delete)
    {
        await PrepareAsync(false);
        if (!Ready || Error is not null) return Page();
        if (!ModelState.IsValid) { Error = "Invalid form values. Review the fields and retry."; return Page(); }
        try
        {
            var fields = Modes.ToDictionary(p => p.Key, p => new FieldInput(p.Value, Values.GetValueOrDefault(p.Key)));
            TempData["Message"] = await tables.SaveAsync(Id, Token, fields, delete, Ct, Db);
            return RedirectToPage("/Browse", new { db=Db, id=Id });
        }
        catch (Exception ex) when (Expected(ex)) { Error = Message(ex); return Page(); }
    }
}
