using AdminerDOTNET.Models;
using AdminerDOTNET.Services;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Data.SqlClient;
using System.Data.SqlTypes;
using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;

namespace AdminerDOTNET.Pages;

public abstract class DatabasePage(DatabaseConnection database, MetadataService metadata) : PageModel
{
    [BindProperty(SupportsGet = true, Name = "db")]
    [DisplayFormat(ConvertEmptyStringToNull = false)]
    public string? Db { get; set; }
    public string DatabaseName => Db ?? database.DatabaseName;
    public List<string> Databases { get; private set; } = [];
    public List<DbObject> Objects { get; private set; } = [];
    public int? SelectedId { get; protected set; }
    public string? Error { get; protected set; }
    protected CancellationToken Ct => HttpContext.RequestAborted;
    protected MetadataService Metadata => metadata;
    protected DatabaseConnection Connection => database;
    protected async Task<bool> LoadAsync()
    {
        try
        {
            Databases = await database.DatabasesAsync(Ct);
            Db = database.Resolve(Db, Databases);
            Objects = await metadata.ObjectsAsync(Ct, Db);
            return true;
        }
        catch (Exception ex) when (Expected(ex)) { Error = Message(ex); return false; }
    }
    protected static bool Expected(Exception ex) => ex is InvalidOperationException or SqlException or FormatException or OverflowException or SqlTypeException or ArgumentException;
    protected async Task<IActionResult> ExportAsync(QueryService queries, CsvExportStore store, Guid id, string sql)
    {
        if (!ModelState.IsValid) return BadRequest(new { error = "Invalid export request. Reload the page and retry." });
        if (!await LoadAsync()) return BadRequest(new { error = Error });
        var result = await queries.ExportAsync(id, sql, store, Ct, Db);
        // Refresh only metadata after execution; never rerun SQL or change the displayed grid.
        await LoadAsync();
        return new JsonResult(new
        {
            result.Error, result.Sets, result.Messages, result.Affected, result.Milliseconds,
            downloadUrl = result.Token is null ? null : Url.Page("/Sql", "Download", new { db = Db, token = result.Token }),
            objects = Objects.Select(obj => new { obj.Id, obj.Schema, obj.Name, obj.IsView }),
            metadataError = Error
        });
    }
    protected static string Message(Exception ex) => ex is SqlException sql ? $"SQL {sql.Number}: {sql.Message}" : ex.Message;
}
