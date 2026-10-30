using AdminerDOTNET.Models;
using AdminerDOTNET.Services;
using Microsoft.AspNetCore.Mvc;

namespace AdminerDOTNET.Pages;

public class StructureModel(DatabaseConnection database, MetadataService metadata) : DatabasePage(database, metadata)
{
    [BindProperty(SupportsGet=true)] public int Id { get; set; }
    public TableMetadata? Table { get; private set; }
    public async Task OnGetAsync()
    {
        SelectedId = Id;
        if (!await LoadAsync()) return;
        try { Table = await Metadata.GetAsync(Id, Ct, Db); }
        catch (Exception ex) when (Expected(ex)) { Error = Message(ex); }
    }
}
