using System.Collections.Concurrent;
using System.Net;
using System.Text.RegularExpressions;
using AdminerDOTNET.Models;
using AdminerDOTNET.Pages;
using AdminerDOTNET.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.WebUtilities;

// Other database names are simulated. Every physical connection still opens AdminerTest.
public sealed class SimulatedDatabaseConnection(IConfiguration configuration) : DatabaseConnection(configuration)
{
    public const string Other = "Other ] ;=\"雪";
    public ConcurrentQueue<string> Requested { get; } = new();
    public bool FailCatalog { get; set; }
    public bool FailOther { get; set; }
    public override Task<List<string>> DatabasesAsync(CancellationToken cancellation = default) =>
        FailCatalog ? throw new InvalidOperationException("Configured default unavailable. Check configuration and retry.")
            : Task.FromResult<List<string>>(["AdminerTest", Other]);

    protected override async Task<SqlConnection> OpenConnectionAsync(SqlConnectionStringBuilder settings, CancellationToken cancellation)
    {
        Requested.Enqueue(settings.InitialCatalog);
        if (FailOther && settings.InitialCatalog == Other) throw new InvalidOperationException("Selected database unavailable. Retry.");
        var physical = Settings();
        if (physical.InitialCatalog != "AdminerTest") throw new InvalidOperationException("Tests must stay inside AdminerTest.");
        var connection = new SqlConnection(physical.ConnectionString);
        try
        {
            await connection.OpenAsync(cancellation);
            using var guard = new SqlCommand("SELECT DB_NAME()", connection);
            if ((string)(await guard.ExecuteScalarAsync(cancellation))! != "AdminerTest") throw new InvalidOperationException("Unexpected physical test database.");
            return connection;
        }
        catch { await connection.DisposeAsync(); throw; }
    }
}

public static class SelectionChecks
{
    public static async Task RunAsync(IConfiguration configuration, int fixtureId, Action<bool,string> check)
    {
        async Task Reject(Func<Task> operation, string message)
        {
            try { await operation(); }
            catch (InvalidOperationException) { check(true, message); return; }
            check(false, message);
        }
        var ct = CancellationToken.None;
        var database = new SimulatedDatabaseConnection(configuration);
        var metadata = new MetadataService(database);
        var protection = new EphemeralDataProtectionProvider();
        var tables = new TableService(database, metadata, protection);
        var queries = new QueryService(database);
        var other = SimulatedDatabaseConnection.Other;
        var available = await database.DatabasesAsync(ct);
        check(database.Resolve(null, available) == "AdminerTest", "Missing selection resolves configured default");
        check(database.Resolve(other, available) == other, "Unusual database names resolve exactly");
        foreach (var invalid in new[] { "", "master", "model", "msdb", "tempdb", "Missing", "AdminerTest;Database=master" })
            await Reject(() => database.OpenAsync(ct, invalid), "Reject unavailable/system selection: " + (invalid == "" ? "(empty)" : invalid));
        check(database.Requested.IsEmpty, "Invalid selections never open a target or fall back");
        var original = database.Settings();
        var selected = database.Settings(other);
        check(selected.InitialCatalog == other && selected.DataSource == original.DataSource && selected.IntegratedSecurity == original.IntegratedSecurity,
            "Connection builder changes only the database without interpreting its punctuation");
        selected.InitialCatalog = original.InitialCatalog;
        check(selected.ConnectionString == original.ConnectionString, "Server, authentication, and other connection settings are retained");
        check(database.Settings().InitialCatalog == "AdminerTest", "Selecting another database never changes configuration");

        var table = await metadata.GetAsync(fixtureId, ct, other);
        var fields = table.Columns.Where(c=>c.Writable).ToDictionary(c=>c.Id,c=>new FieldInput("default",null));
        var textColumn = table.Columns.Single(c=>c.Name=="Text");
        fields[textColumn.Id] = new("value", "selection test");
        await tables.SaveAsync(fixtureId, null, fields, false, ct, other);
        var row = (await tables.BrowseAsync(fixtureId,1,null,false,textColumn.Id,"eq","selection test",ct,other)).Rows.Single();
        tables.Decode(table,row.Token!,other);
        await Reject(() => Task.FromResult(tables.Decode(table,row.Token!,"AdminerTest")), "Row snapshot rejects another database with identical object ID and schema");
        var alternateConfig = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>
        {
            ["ConnectionStrings:DefaultConnection"] = "Server=(localdb)\\AnotherServer;Database=AdminerTest;Integrated Security=true"
        }).Build();
        var alternateTables = new TableService(new DatabaseConnection(alternateConfig),metadata,protection);
        await Reject(() => Task.FromResult(alternateTables.Decode(table,row.Token!,other)), "Row snapshot rejects another server without opening it");
        var refreshed = await tables.RefreshAsync(fixtureId,row.Token!,ct,other);
        fields[textColumn.Id] = new("value", "selection updated");
        await tables.SaveAsync(fixtureId,refreshed,fields,false,ct,other);
        var updated = await tables.RefreshAsync(fixtureId,refreshed,ct,other);
        await tables.SaveAsync(fixtureId,updated,[],true,ct,other);
        var query = await queries.ExecuteAsync(Guid.NewGuid(),"SELECT DB_NAME() AS PhysicalTestDatabase;",ct,other);
        check(query.Error is null && query.Grids.Single().Rows.Single().Single()=="AdminerTest", "Simulated selection executes SQL only in physical AdminerTest");
        check(database.Requested.All(name=>name==other), "Metadata, browse, insert, update, delete, reload, and SQL propagate selection");
        var executionId = Guid.NewGuid();
        var pending = queries.ExecuteAsync(executionId,"WAITFOR DELAY '00:00:20';",ct,other);
        await Task.Delay(200);
        check(!queries.Cancel(executionId,"AdminerTest") && !pending.IsCompleted, "Cancellation cannot target an execution in another database");
        check(queries.Cancel(executionId,other), "Cancellation accepts matching database and execution ID");
        check((await pending).Error?.Contains("cancelled")==true, "Matching selection cancels the SQL operation");
        database.FailOther = true;
        await Reject(() => database.OpenAsync(ct,other), "Database becoming unavailable reports an error without fallback");
        database.FailOther = false;

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ApplicationName=typeof(DatabasePage).Assembly.GetName().Name,
            ContentRootPath=Directory.GetCurrentDirectory(),
            EnvironmentName="Development"
        });
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddRazorPages();
        builder.Services.AddDataProtection().UseEphemeralDataProtectionProvider();
        builder.Services.AddSingleton<DatabaseConnection>(database);
        builder.Services.AddScoped<MetadataService>();
        builder.Services.AddScoped<TableService>();
        builder.Services.AddSingleton<QueryService>();
        await using var app = builder.Build();
        app.UseRouting();
        app.MapRazorPages();
        await app.StartAsync();
        try
        {
            // Clients share cookies, just as separate tabs do. There is no shared selection cookie.
            var cookies = new CookieContainer();
            using var tabA = new HttpClient(new HttpClientHandler { CookieContainer=cookies,AllowAutoRedirect=false }) { BaseAddress=new Uri(app.Urls.Single()) };
            using var tabB = new HttpClient(new HttpClientHandler { CookieContainer=cookies,AllowAutoRedirect=false }) { BaseAddress=tabA.BaseAddress };
            string Decode(string html) => WebUtility.HtmlDecode(html);
            string Token(string html, string name) => Decode(Regex.Match(html,$"name=\"{name}\"[^>]*value=\"([^\"]*)\"").Groups[1].Value);
            var encoded = Uri.EscapeDataString(other);
            bool HasSelection(string url) => QueryHelpers.ParseQuery(Decode(url).Split('?',2).ElementAtOrDefault(1) ?? "").TryGetValue("db",out var db) && db == other;
            bool HasSelectionLink(string html) => Regex.Matches(html,"href=\"([^\"]+)\"").Any(match=>HasSelection(match.Groups[1].Value));
            var first = await tabA.GetStringAsync("/");
            check(first.Contains("id=\"database-select\"") && Decode(first).Contains(other) && !first.Contains("<option value=\"master\""), "Dropdown lists available user databases");
            var second = await tabB.GetStringAsync("/?db="+encoded);
            check(Decode(second).Contains($"<title>Overview · {other} · Adminer.NET"), "Selected database appears in page title");
            check(HasSelectionLink(second), "Navigation URLs encode and retain unusual database names");
            check((await tabA.GetStringAsync("/")).Contains("<title>Overview · AdminerTest · Adminer.NET"), "Separate tab selection does not change default visits");
            var switchTag = Regex.Match(second,@"<form[^>]*id=""database-switch""[^>]*>").Value;
            var switchAction = Decode(Regex.Match(switchTag,"action=\"([^\"]+)\"").Groups[1].Value);
            check(switchAction == "/", "Switch action clears table identity and filters");
            database.Requested.Clear();
            foreach (var path in new[] { "/?db=Missing", "/?db=", "/?db=master" })
            {
                var invalid = await tabA.GetStringAsync(path);
                check(invalid.Contains("selected database is unavailable"), "HTTP invalid selection shows error: " + path);
            }
            check(database.Requested.IsEmpty, "Invalid page requests do not open a default target");
            var browse = await tabB.GetStringAsync($"/Browse?db={encoded}&id={fixtureId}");
            check(HasSelectionLink(browse) && Token(browse,"db")==other, "Browse links, pagination, filters, and record forms carry database selection");
            var structure = await tabB.GetStringAsync($"/Structure?db={encoded}&id={fixtureId}");
            check(HasSelectionLink(structure), "Structure navigation retains selection");
            var insert = await tabB.GetStringAsync($"/Record?db={encoded}&id={fixtureId}");
            var csrf = Token(insert,"__RequestVerificationToken");
            check(Decode(insert).Contains(other+" · dbo.") && Token(insert,"db")==other, "Record heading and hidden field identify selected database");
            var post = new Dictionary<string,string> { ["db"]=other,["Id"]=fixtureId.ToString(),["__RequestVerificationToken"]=csrf };
            foreach(var c in table.Columns.Where(c=>c.Writable))
            {
                post[$"Modes[{c.Id}]"] = c.Name=="Text" ? "value" : "default";
                post[$"Values[{c.Id}]"] = c.Name=="Text" ? "selection HTTP" : "";
            }
            var response = await tabB.PostAsync("/Record?handler=Save",new FormUrlEncodedContent(post));
            check(response.StatusCode==HttpStatusCode.Redirect && HasSelection(response.Headers.Location!.OriginalString), "Record save redirects back to the same database");
            var filtered = await tabB.GetStringAsync($"/Browse?db={encoded}&id={fixtureId}&filter={textColumn.Id}&value=selection%20HTTP&op=eq");
            var rowToken = Token(filtered,"Token");
            post["Token"] = rowToken;
            post["db"] = "AdminerTest";
            var cross = await tabB.PostAsync("/Record?handler=Open",new FormUrlEncodedContent(post));
            check((await cross.Content.ReadAsStringAsync()).Contains("another server/database"), "HTTP rejects replayed record token against another database");
            post["db"] = other;
            var reload = await tabB.PostAsync("/Record?handler=Reload",new FormUrlEncodedContent(post));
            var reloaded = await reload.Content.ReadAsStringAsync();
            check(Token(reloaded,"db")==other && reloaded.Contains("selection HTTP"), "HTTP record reload retains selection");
            post["Token"] = Token(reloaded,"Token");
            var deleted = await tabB.PostAsync("/Record?handler=Delete",new FormUrlEncodedContent(post));
            check(deleted.StatusCode==HttpStatusCode.Redirect && HasSelection(deleted.Headers.Location!.OriginalString), "HTTP delete returns to selected database");

            var sql = await tabB.GetStringAsync("/Sql?db="+encoded);
            check(Token(sql,"db")==other && Decode(sql).Contains(other), "SQL form and heading retain selection");
            var sqlPost = new Dictionary<string,string> { ["db"]=other,["__RequestVerificationToken"]=Token(sql,"__RequestVerificationToken"),["ExecutionId"]=Guid.NewGuid().ToString(),["CommandText"]="SELECT 17 AS SelectionTest;" };
            database.Requested.Clear();
            var sqlResponse = await tabB.PostAsync("/Sql?handler=Run",new FormUrlEncodedContent(sqlPost));
            check((await sqlResponse.Content.ReadAsStringAsync()).Contains("SelectionTest") && database.Requested.All(name=>name==other), "SQL POST carries chosen database through execution and refreshed sidebar");
            sqlPost["db"] = "Missing";
            database.Requested.Clear();
            await tabB.PostAsync("/Sql?handler=Run",new FormUrlEncodedContent(sqlPost));
            check(database.Requested.IsEmpty, "Invalid SQL selection cannot execute in the default database");
            database.FailCatalog = true;
            var failed = await tabA.GetStringAsync("/?db=AdminerTest");
            check(failed.Contains("Configured default unavailable"), "Unavailable discovery default shows guidance without another connection");
            database.FailCatalog = false;
        }
        finally { await app.StopAsync(); }
    }
}
