using AdminerDOTNET.Services;
namespace AdminerDOTNET.Pages;
public class ErrorModel(DatabaseConnection database, MetadataService metadata) : DatabasePage(database, metadata)
{
    public void OnGet() => Error = "An unexpected error occurred. Return to Overview and retry.";
    public void OnPost() => OnGet();
}
