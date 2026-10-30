using System.Net;
using System.Text.RegularExpressions;
using AdminerDOTNET.Models;
using AdminerDOTNET.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;

public static class BrowseChecks
{
    public static async Task RunAsync(IConfiguration configuration, Action<bool,string> check)
    {
        var ct = CancellationToken.None;
        var database = new DatabaseConnection(configuration);
        var metadata = new MetadataService(database);
        var tables = new TableService(database, metadata, new EphemeralDataProtectionProvider());
        var queries = new QueryService(database);
        await using var connection = await database.OpenAsync(ct, "AdminerTest");
        using (var guard = new SqlCommand("SELECT DB_NAME()", connection))
            check((string)(await guard.ExecuteScalarAsync())! == "AdminerTest", "Browse fixtures are confined to AdminerTest");
        async Task Execute(string sql) { using var cmd = new SqlCommand(sql, connection); await cmd.ExecuteNonQueryAsync(); }
        async Task Reject(Func<Task> action, string message)
        {
            try { await action(); }
            catch (Exception ex) when (ex is InvalidOperationException or SqlException or FormatException) { check(true,message); return; }
            check(false,message);
        }
        var suffix = Guid.NewGuid().ToString("N")[..10];
        var name = "AdminerPaging_"+suffix;
        var keylessName = "AdminerKeyless_"+suffix;
        var qualified = "[dbo]."+SqlNames.Quote(name);
        var keylessQualified = "[dbo]."+SqlNames.Quote(keylessName);
        try
        {
            await Execute($"""
                CREATE TABLE {qualified} (Id int IDENTITY PRIMARY KEY, Category nvarchar(40) NULL,
                    Amount decimal(18,2) NOT NULL DEFAULT 1.25, Created datetime2(7) NOT NULL DEFAULT '2026-10-30T12:34:56.1234567',
                    Enabled bit NOT NULL DEFAULT 1, Bytes varbinary(8) NULL);
                INSERT INTO {qualified} (Category,Bytes)
                SELECT CASE WHEN n%2=0 THEN N'even' ELSE N'odd' END,0x0102
                FROM (SELECT TOP(250) ROW_NUMBER() OVER(ORDER BY (SELECT NULL)) n FROM sys.all_columns) a;
                INSERT INTO {qualified} (Category) VALUES (NULL),(N''),(N'O''Brien 雪');
                CREATE TABLE {keylessQualified} (Code numeric(18,0) NOT NULL CHECK (Code>0), Name nchar(10) NULL,
                    Note text NULL, Bin image NULL, Doc xml NULL, Calculated AS (Code+1));
                INSERT INTO {keylessQualified} (Code,Name,Note,Bin,Doc) VALUES (1,N'dup','legacy',0x01,'<r/>'),(1,N'dup','legacy',0x01,'<r/>');
                """);
            var objects = await metadata.ObjectsAsync(ct,"AdminerTest");
            var obj = objects.Single(o=>o.Name==name);
            var table = await metadata.GetAsync(obj.Id,ct,"AdminerTest");
            var keylessObj = objects.Single(o=>o.Name==keylessName);
            FilterNode Condition(string column,string op,string? value=null) => new() { Column=table.Columns.Single(c=>c.Name==column).Id,Operator=op,Value=value };
            FilterNode Group(string logic,params FilterNode[] children) => new() { Logic=logic,Children=children.ToList() };
            Task<BrowseResult> Browse(FilterNode filters,long page=1,int? sort=null,bool desc=false) => tables.BrowseAsync(obj.Id,page,sort,desc,filters,ct,"AdminerTest");
            var all = FilterState.Parse(null);
            var first = await Browse(all);
            check(first.TotalRows==253 && first.MatchingRows==253 && first.Rows.Count==100 && first.TotalPages==3, "Exact total counts and SQL page size");
            var last = await Browse(all,99999);
            check(last.Page==3 && last.Rows.Count==53 && last.FirstRow==201 && last.LastRow==253 && !last.HasNext,"Out-of-range page clamps to last page with correct range");
            check((await Browse(all,-5)).Page==1,"Negative page clamps to first page");
            var nested = Group("and",Condition("Id","gt","100"),Group("or",Condition("Category","eq","even"),Condition("Category","null")));
            var nestedResult = await Browse(nested,1,1,true);
            check(nestedResult.MatchingRows==76 && nestedResult.TotalRows==253 && nestedResult.Rows[0].Values[0]=="251", "Nested AND/OR executes in SQL before descending sort and paging");
            var evens = Condition("Category","eq","even");
            var secondEven = await Browse(evens,2);
            check(secondEven.MatchingRows==125 && secondEven.TotalPages==2 && secondEven.Rows.Count==25 && secondEven.Rows[0].Values[0]=="202", "Filtering covers the full table before SQL pagination");
            var empty = await Browse(Condition("Category","eq","missing"),50);
            check(empty.MatchingRows==0 && empty.TotalPages==0 && empty.Page==1 && empty.FirstRow==0 && empty.Rows.Count==0,"Empty result counts and navigation");
            foreach(var (op,value,count) in new (string,string?,long)[] { ("null",null,1),("empty",null,1),("notempty",null,251),("contains","%_[",0),("contains","even",125),("notcontains","even",127),("starts","ev",125),("ends","ven",125),("eq","O'Brien 雪",1),("eq","' OR 1=1--",0) })
                check((await Browse(Condition("Category",op,value))).MatchingRows==count,"SQL text/null operator: "+op+" "+value);
            foreach(var filter in new[] { nested,Condition("Amount","ge","1.25"),Condition("Created","eq","2026-10-30T12:34:56.1234567"),Condition("Enabled","eq","1"),Condition("Bytes","eq","0x0102"),Condition("Category","eq","O'Brien 雪") })
            {
                var generated = await Browse(filter,1);
                var executed = await queries.ExecuteAsync(Guid.NewGuid(),generated.Sql,ct,"AdminerTest");
                check(executed.Error is null && executed.Grids.Count==1 && executed.Grids[0].Rows.Select(r=>string.Join("|",r)).SequenceEqual(generated.Rows.Select(r=>string.Join("|",r.Values))), "Generated standalone SQL reproduces typed parameterized results");
            }
            await Reject(()=>Browse(new() { Logic="xor",Children=[] }),"Invalid group logic rejected");
            await Reject(()=>Browse(Group("and",Group("or"))),"Empty nested group rejected");
            await Reject(()=>Browse(new() { Column=999,Operator="eq",Value="1" }),"Unknown column rejected");
            await Reject(()=>Browse(Condition("Amount","contains","1")),"Type-inappropriate operator rejected");
            await Reject(()=>Browse(Condition("Amount","eq","not a number")),"Invalid typed filter value rejected");
            await Reject(()=>Task.FromResult(FilterState.Parse("{")),"Malformed JSON filter rejected");
            await Reject(()=>Task.FromResult(FilterState.Parse(new string('x',6001))),"Oversized filter definition rejected");
            await Reject(()=>Browse(Group("and",Enumerable.Range(0,51).Select(_=>Condition("Id","gt","0")).ToArray())),"Excessively complex filter tree rejected");
            var deep = Condition("Id","gt","0");
            for(var i=0;i<10;i++) deep=Group("and",deep);
            await Reject(()=>Browse(deep),"Excessive nesting rejected");
            await Reject(()=>Browse(Group("and",new FilterNode[] { null! })),"Null filter node rejected");

            var kt = await metadata.GetAsync(keylessObj.Id,ct,"AdminerTest");
            check(kt.CanInsert && kt.CanEdit && kt.Keys.Count==0,"Keyless table allows insert and single-row mutations");
            var initial = (await tables.BrowseAsync(kt.Object.Id,1,null,false,null,"eq",null,ct,"AdminerTest")).Rows;
            Dictionary<int,FieldInput> Fields(DataRow row) => kt.Columns.Where(c=>c.Writable).ToDictionary(c=>c.Id,c=>new FieldInput(row.Values[kt.Columns.IndexOf(c)] is null ? "null":"value", row.Values[kt.Columns.IndexOf(c)]));
            var fields = Fields(initial[0]);
            fields[kt.Columns.Single(c=>c.Name=="Name").Id] = new("value","changed");
            await tables.SaveAsync(kt.Object.Id,initial[0].Token,fields,false,ct,"AdminerTest");
            var changed = await tables.BrowseAsync(kt.Object.Id,1,null,false,null,"eq",null,ct,"AdminerTest");
            check(changed.Rows.Count==2 && changed.Rows.Count(r=>r.Values[1]!.Trim()=="changed")==1,"Keyless update changes only one of two identical rows, matching legacy text/image/XML");
            await tables.SaveAsync(kt.Object.Id,initial[1].Token,[],true,ct,"AdminerTest");
            check((await tables.BrowseAsync(kt.Object.Id,1,null,false,null,"eq",null,ct,"AdminerTest")).Rows.Count==1,"Keyless delete removes one matching duplicate");
            await Reject(()=>tables.SaveAsync(kt.Object.Id,initial[0].Token,fields,false,ct,"AdminerTest"),"Keyless stale/missing match is rejected");
            fields[kt.Columns.Single(c=>c.Name=="Code").Id] = new("value","-1");
            await Reject(()=>tables.SaveAsync(kt.Object.Id,null,fields,false,ct,"AdminerTest"),"SQL Server enforces constraints for keyless inserts");
            fields[kt.Columns.Single(c=>c.Name=="Code").Id] = new("value","2");
            await tables.SaveAsync(kt.Object.Id,null,fields,false,ct,"AdminerTest");
            check((await tables.BrowseAsync(kt.Object.Id,1,null,false,null,"eq",null,ct,"AdminerTest")).Rows.Count==2,"Insert succeeds without a primary key");

            using var client = new HttpClient(new HttpClientHandler { CookieContainer=new CookieContainer() }) { BaseAddress=new Uri("http://127.0.0.1:5186") };
            string Token(string html,string name) => WebUtility.HtmlDecode(Regex.Match(html,$"name=\"{name}\"[^>]*value=\"([^\"]*)\"").Groups[1].Value);
            var keylessForm = await client.GetStringAsync($"/Record?db=AdminerTest&id={kt.Object.Id}");
            check(keylessForm.Contains("Insert record") && keylessForm.Contains("Code value"),"Keyless insert form opens without requiring a primary key");
            var keylessPost = new Dictionary<string,string> { ["db"]="AdminerTest",["Id"]=kt.Object.Id.ToString(),["__RequestVerificationToken"]=Token(keylessForm,"__RequestVerificationToken") };
            foreach(var c in kt.Columns.Where(c=>c.Writable))
            {
                keylessPost[$"Modes[{c.Id}]"] = c.Name=="Note" ? "null" : "value";
                keylessPost[$"Values[{c.Id}]"] = c.Name=="Code" ? "3" : c.Name=="Name" ? "webrow" : "";
            }
            var keylessInsert = await client.PostAsync("/Record?handler=Save",new FormUrlEncodedContent(keylessPost));
            check((await keylessInsert.Content.ReadAsStringAsync()).Contains("Record inserted."),"Keyless native form insertion succeeds");
            async Task<string> KeylessToken()
            {
                var browsed = await client.GetStringAsync($"/Browse?db=AdminerTest&id={kt.Object.Id}&filter={kt.Columns.Single(c=>c.Name=="Code").Id}&op=eq&value=3");
                return Token(browsed,"Token");
            }
            keylessPost["Token"] = await KeylessToken();
            var keylessOpen = await client.PostAsync("/Record?handler=Open",new FormUrlEncodedContent(keylessPost));
            check((await keylessOpen.Content.ReadAsStringAsync()).Contains("Identical copies cannot be distinguished"),"Keyless edit form explains one-match semantics");
            keylessPost[$"Values[{kt.Columns.Single(c=>c.Name=="Name").Id}]"]="webedit";
            var keylessSave = await client.PostAsync("/Record?handler=Save",new FormUrlEncodedContent(keylessPost));
            check((await keylessSave.Content.ReadAsStringAsync()).Contains("Record saved."),"Keyless update form saves successfully");
            keylessPost["Token"] = await KeylessToken();
            var keylessDelete = await client.PostAsync("/Record?handler=Delete",new FormUrlEncodedContent(keylessPost));
            check((await keylessDelete.Content.ReadAsStringAsync()).Contains("Record deleted."),"Keyless delete form removes its one matching record");
            var encoded=Uri.EscapeDataString(FilterState.Serialize(evens));
            var response = await client.GetAsync($"/Browse?db=AdminerTest&id={obj.Id}&filters={encoded}&p=2");
            var html=await response.Content.ReadAsStringAsync();
            check(response.Headers.GetValues("Referrer-Policy").Single()=="same-origin","Browser referrer policy preserves native form POST origins");
            check(WebUtility.HtmlDecode(html).Contains("101–125 of 125 matching rows · 253 total rows · Page 2 of 2") && html.Contains("Jump to page"),"Browse displays filtered/table totals and page-jump control");
            var originalSql = WebUtility.HtmlDecode(Regex.Match(html,"<textarea[^>]*id=\"CommandText\"[^>]*>([\\s\\S]*?)</textarea>").Groups[1].Value).TrimStart('\r','\n');
            check(originalSql.Contains("DECLARE @filter0") && html.IndexOf("SQL for this view",StringComparison.Ordinal)<html.IndexOf("filter-builder",StringComparison.Ordinal),"Editable SQL appears above filters with parameter declarations");
            check(Token(html,"desc")=="False" && Token(html,"Desc")=="False", "Filter, paging, and SQL forms submit an explicit false sort direction");
            var post = new Dictionary<string,string> { ["db"]="AdminerTest",["Id"]=obj.Id.ToString(),["P"]="2",["Filters"]=FilterState.Serialize(evens),["__RequestVerificationToken"]=Token(html,"__RequestVerificationToken"),["ExecutionId"]=Guid.NewGuid().ToString(),["CommandText"]=$"SELECT TOP(1) a.Id AS LeftId,b.Category AS JoinedCategory FROM {qualified} a JOIN {qualified} b ON a.Id=b.Id; SELECT 42 AS AnotherShape;" };
            var executedPost = await client.PostAsync("/Browse?handler=Run",new FormUrlEncodedContent(post));
            var custom=await executedPost.Content.ReadAsStringAsync();
            check(custom.Contains("Custom SQL results") && custom.Contains("JoinedCategory") && custom.Contains("AnotherShape") && !custom.Contains("class=\"page-jump\"") && !custom.Contains("+ Insert record") && !custom.Contains("id=\"filters-form\""),"Custom joins/multiple results replace Browse grid and suspend table actions");
            var reset=WebUtility.HtmlDecode(Regex.Match(custom,"id=\"reset-sql\"[^>]*href=\"([^\"]+)\"").Groups[1].Value);
            var resetHtml=await client.GetStringAsync(reset);
            check(resetHtml.Contains("Page 2 of 2") && resetHtml.Contains("id=\"filters-form\"") && !resetHtml.Contains("Custom SQL results"),"Reset restores applied filters/page without rerunning edited SQL");
            post["ExecutionId"]=Guid.NewGuid().ToString(); post["CommandText"]="SELECT * FROM dbo.NoSuchBrowseTable_"+suffix;
            var errored=await client.PostAsync("/Browse?handler=Run",new FormUrlEncodedContent(post));
            check((await errored.Content.ReadAsStringAsync()).Contains("SQL 208"),"Custom SQL errors remain on Browse");
            using var originRequest = new HttpRequestMessage(HttpMethod.Post,"/Browse?handler=Run") { Content=new FormUrlEncodedContent(post) };
            originRequest.Headers.Add("Origin","http://127.0.0.1:5186"); originRequest.Headers.Add("Sec-Fetch-Site","same-origin");
            check((await client.SendAsync(originRequest)).IsSuccessStatusCode,"Native same-origin POST headers are accepted");
            using var nullOrigin = new HttpRequestMessage(HttpMethod.Post,"/Browse?handler=Run") { Content=new FormUrlEncodedContent(post) };
            nullOrigin.Headers.Add("Origin","null");
            check((await client.SendAsync(nullOrigin)).StatusCode==HttpStatusCode.Forbidden,"Opaque/unrelated form origins remain rejected");
            check((await client.GetStringAsync($"/Browse?db=AdminerTest&id={obj.Id}&filters=%7B")).Contains("filter definition is invalid"),"Malformed filters display actionable error on Browse");
        }
        finally
        {
            await Execute($"DROP TABLE IF EXISTS {qualified}; DROP TABLE IF EXISTS {keylessQualified};");
            Console.WriteLine("Removed only Browse verification fixtures inside AdminerTest.");
        }
    }
}
