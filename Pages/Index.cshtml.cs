using AdminerDOTNET.Services;

namespace AdminerDOTNET.Pages;

public class IndexModel(DatabaseConnection database, MetadataService metadata) : DatabasePage(database, metadata)
{
    public async Task OnGetAsync() => await LoadAsync();
}
