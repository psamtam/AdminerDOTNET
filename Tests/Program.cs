using System.Data;
using System.Net;
using System.Text.RegularExpressions;
using AdminerDOTNET.Models;
using AdminerDOTNET.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;

if (args.Contains("--origin-probe")) { await OriginProbe.RunAsync(); return; }
var checks = 0;
void Check(bool condition, string message)
{
    if (!condition) throw new Exception("FAILED: " + message);
    Console.WriteLine("PASS: " + message); checks++;
}
ConnectionConfigurationChecks.Run(Check);
async Task Reject(Func<Task> action, string message)
{
    try { await action(); } catch (InvalidOperationException) { Check(true,message); return; }
    throw new Exception("FAILED: " + message);
}
var config = new ConfigurationBuilder().AddJsonFile(Path.GetFullPath("appsettings.json"))
    .AddInMemoryCollection(new Dictionary<string,string?> { ["ConnectionStrings:DefaultConnection"] = "Server=(localdb)\\MSSQLLocalDB;Database=AdminerTest;Integrated Security=True;TrustServerCertificate=True;" }).Build();
Check(new SqlConnectionStringBuilder(config.GetConnectionString("DefaultConnection")).InitialCatalog == "AdminerTest", "Configuration override selects AdminerTest");
var database = new DatabaseConnection(config);
var metadata = new MetadataService(database);
var tables = new TableService(database, metadata, new EphemeralDataProtectionProvider());
var queries = new QueryService(database);
var ct = CancellationToken.None;
await using var connection = await database.OpenAsync(ct);
async Task Execute(string sql)
{
    using var command = new SqlCommand(sql, connection);
    await command.ExecuteNonQueryAsync();
}
using (var guard = new SqlCommand("SELECT DB_NAME()",connection))
    Check((string)(await guard.ExecuteScalarAsync())! == "AdminerTest", "Actual connection is confined to AdminerTest");
Check(SqlNames.Quote("a]b") == "[a]]b]", "Identifiers escape closing brackets");
await Reject(async () => { await using var ignored = await new DatabaseConnection(new ConfigurationBuilder().Build()).OpenAsync(); }, "Missing configuration has setup error");
await Reject(async () => { await using var ignored = await new DatabaseConnection(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> { ["ConnectionStrings:DefaultConnection"]="Server=(localdb)\\MSSQLLocalDB" }).Build()).OpenAsync(); }, "Database is required instead of defaulting to another database");
await Reject(async () => { await using var ignored = await new DatabaseConnection(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> { ["ConnectionStrings:DefaultConnection"]="Server=tcp:127.0.0.1,1;Database=AdminerTest;Integrated Security=true;Connect Timeout=1" }).Build()).OpenAsync(); }, "Connection failure is actionable");

var suffix = Guid.NewGuid().ToString("N")[..10];
var name = "Adminer verify ] " + suffix;
var plainName = "AdminerPlain_" + suffix;
var viewName = "AdminerView_" + suffix;
var qualified = "[dbo]." + SqlNames.Quote(name);
var plain = "[dbo]." + SqlNames.Quote(plainName);
var view = "[dbo]." + SqlNames.Quote(viewName);
try
{
    await Execute($"""
        CREATE TABLE {qualified} (
          [Key ] value] int NOT NULL, [OtherKey] nvarchar(20) NOT NULL,
          [Name] nchar(10) NULL, [Amount] numeric(38,0) NOT NULL DEFAULT 99999999999999999999999999999999999999,
          [Empty] nvarchar(100) NOT NULL DEFAULT N'fallback', [Created] datetime2(7) NOT NULL DEFAULT sysdatetime(),
          [Token] uniqueidentifier NOT NULL DEFAULT newid(), [Enabled] bit NOT NULL DEFAULT 1,
          [Bytes] varbinary(10) NULL, [Calculated] AS ([Key ] value]+1), [Version] rowversion,
          PRIMARY KEY ([Key ] value],[OtherKey])
        );
        CREATE TABLE {plain} ([Id] int IDENTITY PRIMARY KEY,[Text] nvarchar(100) NULL,[Amount] decimal(18,2) NOT NULL DEFAULT 2.50);
        INSERT INTO {plain} ([Text]) VALUES (N'initial');
        """.Replace("[Key ] value]", "[Key ]] value]"));
    await Execute($"CREATE VIEW {view} AS SELECT [Id],[Text] FROM {plain};");
    var objects = await metadata.ObjectsAsync(ct);
    var obj = objects.Single(o => o.Name == name);
    var plainObj = objects.Single(o => o.Name == plainName);
    var table = await metadata.GetAsync(obj.Id,ct);
    Check(table.Keys.Count == 2 && table.CanEdit, "Composite keys and bracketed identifiers are discovered");
    Check(table.Columns.Any(c => c.IsVersion) && table.Columns.Any(c => c.Computed) && table.Indexes.Count > 0, "Generated fields and indexes are inspected");
    Check(!(await metadata.GetAsync(objects.Single(o => o.Name == viewName).Id,ct)).CanEdit, "Views are read-only in forms");
    var dummy = objects.SingleOrDefault(o => o.Name == "TestTable" && o.Schema == "dbo");
    if (dummy is not null)
    {
        var d = await metadata.GetAsync(dummy.Id,ct);
        Check(d.Columns.Any(c=>c.Type == "numeric" && c.Precision == 18) && d.Columns.Any(c=>c.Type=="nchar"), "Supplied TestTable metadata is read accurately");
    }
    Dictionary<int,FieldInput> Fields(TableMetadata t) => t.Columns.Where(c=>c.Writable).ToDictionary(c=>c.Id,c=>new FieldInput("default",null));
    var fields = Fields(table);
    fields[table.Columns.Single(c=>c.KeyOrder==1).Id] = new("value","1");
    fields[table.Columns.Single(c=>c.KeyOrder==2).Id] = new("value","O'Brien");
    fields[table.Columns.Single(c=>c.Name=="Name").Id] = new("null",null);
    fields[table.Columns.Single(c=>c.Name=="Empty").Id] = new("value","");
    fields[table.Columns.Single(c=>c.Name=="Bytes").Id] = new("value","0x0102FF");
    await tables.SaveAsync(obj.Id,null,fields,false,ct);
    var browse = await tables.BrowseAsync(obj.Id,1,null,false,null,"eq",null,ct);
    var row = browse.Rows.Single();
    string? Cell(string column) => row.Values[table.Columns.FindIndex(c=>c.Name==column)];
    Check(Cell("Name") is null && Cell("Empty")=="" && Cell("Amount")==new string('9',38), "NULL, empty text, omitted defaults, and precision 38 survive insertion");
    Check(Cell("Bytes")=="0x0102FF" && Guid.TryParse(Cell("Token"),out _), "Binary and generated GUID defaults round-trip");
    fields[table.Columns.Single(c=>c.Name=="Name").Id] = new("value","雪");
    await tables.SaveAsync(obj.Id,row.Token,fields,false,ct);
    await Reject(()=>tables.SaveAsync(obj.Id,row.Token,fields,false,ct), "Rowversion rejects stale updates");
    var fresh = await tables.RefreshAsync(obj.Id,row.Token!,ct);
    await tables.SaveAsync(obj.Id,fresh,fields,true,ct);
    Check((await tables.BrowseAsync(obj.Id,1,null,false,null,"eq",null,ct)).Rows.Count==0,"Confirmed service deletion targets exactly one composite-key record");
    await Reject(()=>tables.SaveAsync(obj.Id,fresh,fields,true,ct),"Deleted record cannot be deleted again");
    await Reject(()=>tables.SaveAsync(plainObj.Id,fresh,[],true,ct),"Snapshot cannot be used against another table");
    await Reject(()=>tables.SaveAsync(obj.Id,fresh[..^3]+"xxx",fields,true,ct),"Tampered snapshot is rejected");

    var pt = await metadata.GetAsync(plainObj.Id,ct);
    var initial = (await tables.BrowseAsync(plainObj.Id,1,null,false,null,"eq",null,ct)).Rows.Single();
    await Execute($"UPDATE {plain} SET [Text]=N'concurrent';");
    await Reject(()=>tables.SaveAsync(plainObj.Id,initial.Token,Fields(pt),false,ct),"Original-value comparison rejects stale edits without rowversion");
    var latest = await tables.RefreshAsync(plainObj.Id,initial.Token!,ct);
    var changes = Fields(pt);
    changes[pt.Columns.Single(c=>c.Name=="Text").Id] = new("value","x'; DROP TABLE imaginary;--");
    changes[pt.Columns.Single(c=>c.Name=="Amount").Id] = new("value","1234567890123456.78");
    await tables.SaveAsync(plainObj.Id,latest,changes,false,ct);
    var changed = (await tables.BrowseAsync(plainObj.Id,1,null,false,null,"eq",null,ct)).Rows.Single();
    Check(changed.Values[1]=="x'; DROP TABLE imaginary;--" && changed.Values[2]=="1234567890123456.78", "Parameterized edits preserve hostile text and decimal precision");
    Check((await tables.BrowseAsync(plainObj.Id,1,null,false,pt.Columns[1].Id,"contains","DROP TABLE",ct)).Rows.Count==1,"Typed contains filter");
    Check((await tables.BrowseAsync(plainObj.Id,1,null,false,pt.Columns[2].Id,"gt","100",ct)).Rows.Count==1,"Typed numeric filter");
    Check((await tables.BrowseAsync(plainObj.Id,1,null,false,pt.Columns[1].Id,"null",null,ct)).Rows.Count==0,"NULL filter");
    await Execute($"INSERT INTO {plain} ([Text]) SELECT TOP (1100) N'page' FROM {plain} a CROSS JOIN (SELECT TOP(1100) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) n FROM sys.all_columns) b;");
    var firstPage = await tables.BrowseAsync(plainObj.Id,1,null,false,null,"eq",null,ct);
    var secondPage = await tables.BrowseAsync(plainObj.Id,2,null,false,null,"eq",null,ct);
    Check(firstPage.Rows.Count==100 && firstPage.HasNext && firstPage.Rows[0].Values[0]!=secondPage.Rows[0].Values[0],"Pagination has stable primary-key ordering");
    Check((await tables.BrowseAsync(plainObj.Id,1,pt.Columns[0].Id,true,null,"eq",null,ct)).Rows[0].Values[0]=="1101","Descending sort");
    var result = await queries.ExecuteAsync(Guid.NewGuid(),$"PRINT 'hello'; SELECT 1 AS A; SELECT * FROM {plain}; UPDATE {plain} SET [Text]=[Text] WHERE Id=1;",ct);
    Check(result.Error is null && result.Grids.Count==2 && result.Messages.Contains("hello") && result.Affected==1,"Multiple SQL result sets, messages, and affected counts");
    Check(result.Grids[1].Rows.Count==1000 && result.Grids[1].Truncated,"SQL result row limit");
    var wide = await queries.ExecuteAsync(Guid.NewGuid(),$"SELECT *,*,*,* FROM {plain};",ct);
    Check(wide.Grids[0].Rows.Count==833 && wide.Grids[0].Truncated,"10,000-cell total limit");
    var error = await queries.ExecuteAsync(Guid.NewGuid(),"SELECT * FROM [dbo].[NoSuchAdminerTable_"+suffix+"];",ct);
    Check(error.Error?.Contains("SQL 208") == true,"SQL errors retain number and message");
    Check((await queries.ExecuteAsync(Guid.NewGuid(),"SELECT 1;\nGO\nSELECT 2;",ct)).Error?.Contains("GO") == true,"GO separator explains batch limitation");
    var cancelId = Guid.NewGuid();
    var pending = queries.ExecuteAsync(cancelId,"WAITFOR DELAY '00:00:20'; SELECT 1;",ct);
    await Task.Delay(500);
    Check(queries.Cancel(cancelId),"Cancel finds active execution");
    Check((await pending).Error?.Contains("cancelled") == true,"Cancellation interrupts the server command");
    if (!args.Contains("--skip-timeout"))
    {
        Console.WriteLine("Checking the actual 60-second command timeout…");
        Check((await queries.ExecuteAsync(Guid.NewGuid(),"WAITFOR DELAY '00:01:05';",ct)).Error?.Contains("timeout") == true,"60-second SQL timeout");
    }
    await Execute($"ALTER TABLE {plain} ADD [NewColumn] int NULL;");
    await Reject(()=>tables.SaveAsync(plainObj.Id,changed.Token,changes,false,ct),"Schema changes invalidate older edit forms");

    using var client = new HttpClient(new HttpClientHandler { CookieContainer=new CookieContainer() }) { BaseAddress=new Uri("http://127.0.0.1:5186") };
    var home = await client.GetStringAsync("/?db=AdminerTest");
    Check(home.Contains("AdminerTest") && !home.Contains("Cannot connect"),"HTTP overview renders configured database");
    var structure = await client.GetStringAsync("/Structure?db=AdminerTest&id="+obj.Id);
    Check(structure.Contains("numeric(38,0)") && structure.Contains("PRIMARY KEY"),"Structure Razor page renders metadata");
    var insert = await client.GetStringAsync("/Record?db=AdminerTest&id="+plainObj.Id);
    Check(insert.Contains("Insert record") && insert.Contains("Modes["),"Insert form renders typed field modes");
    var csrf = Regex.Match(insert,"name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;
    Check(!string.IsNullOrEmpty(csrf),"Antiforgery token issued");
    // Submit real browser forms, including the protected token returned by Browse.
    var postFields = new Dictionary<string,string> { ["db"]="AdminerTest",["__RequestVerificationToken"]=csrf,["Id"]=plainObj.Id.ToString() };
    var formTable = await metadata.GetAsync(plainObj.Id,ct);
    foreach (var c in formTable.Columns.Where(c=>c.Writable))
    {
        postFields[$"Modes[{c.Id}]"] = c.Name == "Text" ? "value" : "default";
        postFields[$"Values[{c.Id}]"] = c.Name == "Text" ? "HTTP verify" : "";
    }
    var inserted = await client.PostAsync("/Record?handler=Save",new FormUrlEncodedContent(postFields));
    var insertedHtml = await inserted.Content.ReadAsStringAsync();
    Check(inserted.IsSuccessStatusCode && insertedHtml.Contains("Record inserted."),"HTTP insert form redirects with success message");
    async Task<string> HttpToken(string text)
    {
        var browseHtml = await client.GetStringAsync($"/Browse?db=AdminerTest&id={plainObj.Id}&filter={formTable.Columns.Single(c=>c.Name=="Text").Id}&op=eq&value={Uri.EscapeDataString(text)}");
        return WebUtility.HtmlDecode(Regex.Match(browseHtml,"name=\"Token\"[^>]*value=\"([^\"]+)\"").Groups[1].Value);
    }
    var httpToken = await HttpToken("HTTP verify");
    Check(httpToken.Length>0,"Browse issues a protected HTTP row token");
    var opened = await client.PostAsync("/Record?handler=Open",new FormUrlEncodedContent(new Dictionary<string,string>{["db"]="AdminerTest",["__RequestVerificationToken"]=csrf,["Id"]=plainObj.Id.ToString(),["Token"]=httpToken}));
    var openedHtml = await opened.Content.ReadAsStringAsync();
    Check(opened.IsSuccessStatusCode && openedHtml.Contains("Edit record") && openedHtml.Contains("HTTP verify"),"HTTP protected edit form opens");
    postFields["Token"] = httpToken;
    postFields[$"Values[{formTable.Columns.Single(c=>c.Name=="Text").Id}]"] = "HTTP updated";
    var saved = await client.PostAsync("/Record?handler=Save",new FormUrlEncodedContent(postFields));
    Check((await saved.Content.ReadAsStringAsync()).Contains("Record saved."),"HTTP update form saves values");
    postFields["Token"] = await HttpToken("HTTP updated");
    var deleted = await client.PostAsync("/Record?handler=Delete",new FormUrlEncodedContent(postFields));
    Check((await deleted.Content.ReadAsStringAsync()).Contains("Record deleted."),"HTTP delete form removes selected row");
    var sql = await client.GetStringAsync("/Sql?db=AdminerTest");
    csrf = Regex.Match(sql,"name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;
    var response = await client.PostAsync("/Sql?handler=Run",new FormUrlEncodedContent(new Dictionary<string,string> { ["db"]="AdminerTest",["__RequestVerificationToken"]=csrf,["CommandText"]="SELECT N'<script>alert(1)</script>' AS [Safe];",["ExecutionId"]=Guid.NewGuid().ToString() }));
    var html = await response.Content.ReadAsStringAsync();
    Check(response.IsSuccessStatusCode && html.Contains("&lt;script&gt;"),"SQL POST renders encoded output");
    Check((await client.PostAsync("/Sql?handler=Run",new FormUrlEncodedContent([]))).StatusCode==HttpStatusCode.BadRequest,"POST without antiforgery token is rejected");
    using var crossOrigin = new HttpRequestMessage(HttpMethod.Post,"/Sql?handler=Run") { Content=new FormUrlEncodedContent(new Dictionary<string,string>{ ["__RequestVerificationToken"]=csrf }) };
    crossOrigin.Headers.Add("Origin","https://example.com");
    Check((await client.SendAsync(crossOrigin)).StatusCode==HttpStatusCode.Forbidden,"Cross-origin POST rejected");
    var databases = await database.DatabasesAsync(ct);
    Check(databases.Contains("AdminerTest") && !databases.Any(name=>new[]{"master","model","msdb","tempdb"}.Contains(name)), "Live catalogue lists accessible online user databases without opening them");
    await SelectionChecks.RunAsync(config,plainObj.Id,Check);
    await BrowseChecks.RunAsync(config,Check);
    await BulkDeleteChecks.RunAsync(config,Check);
    await CompletionMetadataChecks.RunAsync(config,Check);
    Console.WriteLine($"SUCCESS: {checks} checks passed.");
}
finally
{
    await Execute($"DROP VIEW IF EXISTS {view}; DROP TABLE IF EXISTS {qualified}; DROP TABLE IF EXISTS {plain};");
    Console.WriteLine("Removed only disposable verification objects inside AdminerTest; database retained.");
}
