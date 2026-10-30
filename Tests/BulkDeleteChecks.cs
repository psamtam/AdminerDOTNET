using System.Net;
using System.Text.RegularExpressions;
using AdminerDOTNET.Models;
using AdminerDOTNET.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;

public static class BulkDeleteChecks
{
    public static async Task RunAsync(IConfiguration configuration, Action<bool,string> check)
    {
        var ct = CancellationToken.None;
        var database = new DatabaseConnection(configuration);
        var metadata = new MetadataService(database);
        var protection = new EphemeralDataProtectionProvider();
        var tables = new TableService(database,metadata,protection);
        await using var connection = await database.OpenAsync(ct,"AdminerTest");
        async Task Execute(string sql) { using var command=new SqlCommand(sql,connection); await command.ExecuteNonQueryAsync(); }
        async Task<long> Scalar(string sql) { using var command=new SqlCommand(sql,connection); return Convert.ToInt64(await command.ExecuteScalarAsync()); }
        async Task Reject(Func<Task> action,string message)
        {
            try { await action(); }
            catch(Exception ex) when(ex is InvalidOperationException or SqlException) { check(true,message); return; }
            check(false,message);
        }
        using(var guard=new SqlCommand("SELECT DB_NAME()",connection)) check((string)(await guard.ExecuteScalarAsync())! == "AdminerTest","Bulk-delete fixtures are confined to AdminerTest");
        var suffix=Guid.NewGuid().ToString("N")[..10];
        var name="AdminerBulk_"+suffix;
        var keylessName="AdminerBulkKeyless_"+suffix;
        var parent="[dbo]."+SqlNames.Quote(name);
        var child="[dbo]."+SqlNames.Quote(name+"_child");
        var keyless="[dbo]."+SqlNames.Quote(keylessName);
        var view="[dbo]."+SqlNames.Quote(name+"_view");
        var unsupported="[dbo]."+SqlNames.Quote(name+"_unsupported");
        try
        {
            await Execute($"""
                CREATE TABLE {parent} (Id int PRIMARY KEY, Name nvarchar(40) NOT NULL, Version rowversion);
                INSERT INTO {parent}(Id,Name) SELECT TOP(205) ROW_NUMBER() OVER(ORDER BY (SELECT NULL)),N'bulk' FROM sys.all_columns;
                CREATE TABLE {child} (ParentId int NOT NULL REFERENCES {parent}(Id));
                INSERT INTO {child}(ParentId) VALUES(2);
                CREATE TABLE {keyless} (Value int NULL, Name nchar(10) NULL);
                INSERT INTO {keyless}(Value,Name) VALUES(1,N'dup'),(1,N'dup'),(1,N'dup');
                CREATE TABLE {unsupported}(Value sql_variant NULL);
                """);
            await Execute($"CREATE VIEW {view} AS SELECT Id,Name FROM {parent};");
            var objects=await metadata.ObjectsAsync(ct,"AdminerTest");
            var id=objects.Single(o=>o.Name==name).Id;
            var kid=objects.Single(o=>o.Name==keylessName).Id;
            var vid=objects.Single(o=>o.Name==name+"_view").Id;
            var uid=objects.Single(o=>o.Name==name+"_unsupported").Id;
            Task<BrowseResult> Browse(int objectId,int page=1) => tables.BrowseAsync(objectId,page,null,false,null,"eq",null,ct,"AdminerTest");
            var page=await Browse(id);
            var one=page.Rows[0].Token!;
            var two=page.Rows[1].Token!;
            var three=page.Rows[2].Token!;
            await Reject(()=>tables.DeleteRowsAsync(id,[],ct,"AdminerTest"),"Empty bulk selection rejected");
            await Reject(()=>tables.DeleteRowsAsync(id,Enumerable.Repeat(one,101).ToList(),ct,"AdminerTest"),"Selection larger than current-page limit rejected");
            await Reject(()=>tables.DeleteRowsAsync(id,[one,one],ct,"AdminerTest"),"Repeated identical row token rejected");
            var anotherOne=(await Browse(id)).Rows[0].Token!;
            await Reject(()=>tables.DeleteRowsAsync(id,[one,anotherOne],ct,"AdminerTest"),"Different tokens for the same primary key rejected");
            await Reject(()=>tables.DeleteRowsAsync(id,[one,"tampered"],ct,"AdminerTest"),"Invalid token rejects entire batch before mutation");
            check(await Scalar($"SELECT COUNT_BIG(*) FROM {parent}")==205,"Invalid selections leave all records intact");
            await Reject(()=>tables.DeleteRowsAsync(id,[one,two],ct,"AdminerTest"),"Foreign-key failure rejects the batch");
            check(await Scalar($"SELECT COUNT_BIG(*) FROM {parent} WHERE Id IN(1,2)")==2,"Constraint failure rolls back an earlier deletion");
            await Execute($"UPDATE {parent} SET Name=N'changed' WHERE Id=3;");
            await Reject(()=>tables.DeleteRowsAsync(id,[one,three],ct,"AdminerTest"),"Changed rowversion rejects bulk deletion");
            check(await Scalar($"SELECT COUNT_BIG(*) FROM {parent} WHERE Id=1")==1,"Stale snapshot rolls back an earlier deletion");
            await Execute($"DELETE FROM {parent} WHERE Id=4;");
            await Reject(()=>tables.DeleteRowsAsync(id,[one,page.Rows[3].Token!],ct,"AdminerTest"),"Deleted row rejects bulk deletion");
            check(await Scalar($"SELECT COUNT_BIG(*) FROM {parent} WHERE Id=1")==1,"Missing record rolls back an earlier deletion");
            var wrongScope=new TableService(new DatabaseConnection(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> { ["ConnectionStrings:DefaultConnection"]="Server=(localdb)\\OtherServer;Database=AdminerTest;Integrated Security=true" }).Build()),metadata,protection);
            var table=await metadata.GetAsync(id,ct,"AdminerTest");
            await Reject(()=>Task.FromResult(wrongScope.Decode(table,one,"AdminerTest")),"Cross-server bulk snapshot rejected without opening another server");
            var foreign=protection.CreateProtector("AdminerDOTNET.Rows.v2",database.ContextIdentity("SimulatedOtherDatabase"))
                .Protect(System.Text.Json.JsonSerializer.Serialize(new RowSnapshot(table.Object.Id,System.Text.Json.JsonSerializer.Serialize(table.Columns),page.Rows[0].Values)));
            await Reject(()=>tables.DeleteRowsAsync(id,[one,foreign],ct,"AdminerTest"),"Cross-database token rejects the entire selection without opening another database");
            await Reject(()=>tables.DeleteRowsAsync(vid,[one],ct,"AdminerTest"),"Views reject bulk deletion");
            await Reject(()=>tables.DeleteRowsAsync(uid,[one],ct,"AdminerTest"),"Unsupported row-matching types reject bulk deletion");
            await Execute($"ALTER TABLE {parent} ADD Added int NULL;");
            await Reject(()=>tables.DeleteRowsAsync(id,[one],ct,"AdminerTest"),"Changed schema rejects bulk tokens");
            page=await Browse(id);
            check(await tables.DeleteRowsAsync(id,[page.Rows[4].Token!,page.Rows[5].Token!],ct,"AdminerTest")==2,"Keyed bulk deletion returns exact deleted count");
            var duplicates=(await Browse(kid)).Rows;
            check(duplicates.Select(r=>r.Token).Distinct().Count()==3,"Identical keyless copies have distinct protected selection tokens");
            check(await tables.DeleteRowsAsync(kid,[duplicates[0].Token!,duplicates[1].Token!],ct,"AdminerTest")==2 && await Scalar($"SELECT COUNT_BIG(*) FROM {keyless}")==1,"Keyless bulk deletion preserves selected duplicate multiplicity");
            await Reject(()=>tables.DeleteRowsAsync(kid,[duplicates[0].Token!,duplicates[1].Token!],ct,"AdminerTest"),"Insufficient matching keyless copies reject the batch");
            check(await Scalar($"SELECT COUNT_BIG(*) FROM {keyless}")==1,"Keyless missing match rolls back an earlier copy deletion");

            using var client=new HttpClient(new HttpClientHandler { CookieContainer=new CookieContainer(),AllowAutoRedirect=false }) { BaseAddress=new Uri("http://127.0.0.1:5186") };
            string Token(string html,string name) => WebUtility.HtmlDecode(Regex.Match(html,$"name=\"{name}\"[^>]*value=\"([^\"]*)\"").Groups[1].Value);
            List<string> SelectionTokens(string html) => Regex.Matches(html,"name=\"SelectedTokens\"[^>]*value=\"([^\"]+)\"").Select(m=>WebUtility.HtmlDecode(m.Groups[1].Value)).ToList();
            var html=await client.GetStringAsync($"/Browse?db=AdminerTest&id={id}");
            check(html.Contains("id=\"select-page\"") && html.Contains("id=\"delete-selected\"") && SelectionTokens(html).Count==100,"Browse renders selection only for the current page");
            check(html.Contains("form=\"bulk-delete-form\"") && html.Contains("id=\"bulk-delete-form\""),"Row checkboxes target the separate bulk form without nesting Edit forms");
            var forms=new List<KeyValuePair<string,string>> { new("db","AdminerTest"),new("Id",id.ToString()),new("P","1"),new("Desc","False"),new("__RequestVerificationToken",Token(html,"__RequestVerificationToken")) };
            // First row deletes successfully; second row is referenced by the child and forces a rollback.
            forms.Add(new("SelectedTokens",SelectionTokens(html)[0])); forms.Add(new("SelectedTokens",SelectionTokens(html)[1]));
            using(var native=new HttpRequestMessage(HttpMethod.Post,"/Browse?handler=DeleteSelected") { Content=new FormUrlEncodedContent(forms) })
            {
                native.Headers.Add("Origin","http://127.0.0.1:5186"); native.Headers.Add("Sec-Fetch-Site","same-origin");
                var failed=await client.SendAsync(native);
                var failurePage=await client.GetStringAsync(failed.Headers.Location!);
                check(failurePage.Contains("No selected rows were deleted.") && await Scalar($"SELECT COUNT_BIG(*) FROM {parent} WHERE Id=1")==1,"HTTP failure reports atomic rollback and returns normal Browse");
            }
            var filters=FilterState.Serialize(new FilterNode { Column=1,Operator="ge",Value="100" });
            var url=$"/Browse?db=AdminerTest&id={id}&sort=1&desc=True&p=2&filters={Uri.EscapeDataString(filters)}";
            html=await client.GetStringAsync(url);
            var selectedTokens=SelectionTokens(html);
            check(selectedTokens.Count>0 && selectedTokens.Count<100,"Filtered last page exposes only its displayed tokens");
            forms=[new("db","AdminerTest"),new("Id",id.ToString()),new("P","2"),new("Sort","1"),new("Desc","True"),new("Filters",filters),new("__RequestVerificationToken",Token(html,"__RequestVerificationToken"))];
            forms.AddRange(selectedTokens.Select(token=>new KeyValuePair<string,string>("SelectedTokens",token)));
            var deleted=await client.PostAsync("/Browse?handler=DeleteSelected",new FormUrlEncodedContent(forms));
            var location=deleted.Headers.Location!.OriginalString;
            check(deleted.StatusCode==HttpStatusCode.Redirect && location.Contains("db=AdminerTest") && location.Contains("sort=1") && location.Contains("desc=True") && location.Contains("filters="),"Bulk success preserves database, filters, sorting, and requested page");
            var after=await client.GetStringAsync(location);
            check(after.Contains($"Deleted {selectedTokens.Count} selected row(s).") && after.Contains("Page 1 of 1"),"After deleting last page, Browse clamps page and reports deleted count");
            check((await client.PostAsync("/Browse?handler=DeleteSelected",new FormUrlEncodedContent([]))).StatusCode==HttpStatusCode.BadRequest,"Bulk handler requires antiforgery validation");
            var viewHtml=await client.GetStringAsync($"/Browse?db=AdminerTest&id={vid}");
            check(!viewHtml.Contains("id=\"bulk-delete-form\"") && !viewHtml.Contains("class=\"row-select\""),"Views omit bulk selection controls");
            var unsupportedHtml=await client.GetStringAsync($"/Browse?db=AdminerTest&id={uid}");
            check(!unsupportedHtml.Contains("id=\"select-page\""),"Unmatchable tables omit bulk selection controls");
            var emptyHtml=await client.GetStringAsync($"/Browse?db=AdminerTest&id={id}&filter=1&op=eq&value=-1");
            check(Regex.IsMatch(emptyHtml,"<input[^>]*id=\"select-page\"[^>]*disabled"),"Empty results disable Select all");
            html=await client.GetStringAsync($"/Browse?db=AdminerTest&id={id}");
            var custom=await client.PostAsync("/Browse?handler=Run",new FormUrlEncodedContent(new Dictionary<string,string> { ["db"]="AdminerTest",["Id"]=id.ToString(),["CommandText"]="SELECT 1 AS CustomResult",["ExecutionId"]=Guid.NewGuid().ToString(),["__RequestVerificationToken"]=Token(html,"__RequestVerificationToken") }));
            var customHtml=await custom.Content.ReadAsStringAsync();
            check(customHtml.Contains("Custom SQL results") && !customHtml.Contains("id=\"bulk-delete-form\"") && !customHtml.Contains("id=\"select-page\""),"Custom SQL results omit bulk selection/deletion");
        }
        finally
        {
            await Execute($"DROP VIEW IF EXISTS {view}; DROP TABLE IF EXISTS {child}; DROP TABLE IF EXISTS {parent}; DROP TABLE IF EXISTS {keyless}; DROP TABLE IF EXISTS {unsupported};");
            Console.WriteLine("Removed only bulk-delete verification fixtures inside AdminerTest.");
        }
    }
}
