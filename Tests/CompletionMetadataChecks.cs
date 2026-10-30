using System.Net;
using System.Text.Json;
using AdminerDOTNET.Models;
using AdminerDOTNET.Services;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;

public static class CompletionMetadataChecks
{
    public static async Task RunAsync(IConfiguration configuration,Action<bool,string> check)
    {
        var ct=CancellationToken.None;
        var connectionFactory=new DatabaseConnection(configuration);
        var metadata=new MetadataService(connectionFactory);
        await using var connection=await connectionFactory.OpenAsync(ct,"AdminerTest");
        using(var guard=new SqlCommand("SELECT DB_NAME()",connection)) check((string)(await guard.ExecuteScalarAsync())! == "AdminerTest","Completion fixtures are confined to AdminerTest");
        var name="AdminerCompletion_"+Guid.NewGuid().ToString("N")[..10];
        var qualified="[dbo]."+SqlNames.Quote(name);
        var view="[dbo]."+SqlNames.Quote(name+"View");
        async Task Execute(string sql) { using var cmd=new SqlCommand(sql,connection); await cmd.ExecuteNonQueryAsync(); }
        var sentinel="OnlyRowData_"+Guid.NewGuid().ToString("N");
        try
        {
            await Execute($"CREATE TABLE {qualified}(Id int PRIMARY KEY,PublicName nvarchar(60) NULL); INSERT INTO {qualified}(Id,PublicName) VALUES(1,N'{sentinel}');");
            await Execute($"CREATE VIEW {view} AS SELECT Id,PublicName FROM {qualified};");
            var catalog=await metadata.CompletionsAsync(ct,"AdminerTest");
            var table=catalog.Objects.Single(o=>o.Name==name);
            check(catalog.Database=="AdminerTest" && table.Schema=="dbo" && table.Columns.Single(c=>c.Name=="PublicName").Type=="nvarchar(60)","Completion service returns object/schema/column types for selected database");
            check(catalog.Objects.Single(o=>o.Name==name+"View").IsView,"Views participate in completion metadata");
            check(!JsonSerializer.Serialize(catalog).Contains(sentinel),"Completion service never returns table row data");
            await Execute($"ALTER TABLE {qualified} ADD NewColumn decimal(18,2) NULL;");
            check((await metadata.CompletionsAsync(ct,"AdminerTest")).Objects.Single(o=>o.Name==name).Columns.Any(c=>c.Name=="NewColumn"),"Refreshing completion metadata picks up DDL");
            using var client=new HttpClient { BaseAddress=new Uri("http://127.0.0.1:5186") };
            var response=await client.GetAsync("/Sql?handler=Completions&db=AdminerTest");
            var json=await response.Content.ReadAsStringAsync();
            var payload=JsonSerializer.Deserialize<CompletionMetadata>(json,new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
            check(response.IsSuccessStatusCode && payload.Database=="AdminerTest" && payload.Objects.Any(o=>o.Name==name),"Read-only completion endpoint exposes selected database metadata");
            check(!json.Contains(sentinel) && !json.Contains("ConnectionStrings") && response.Headers.CacheControl?.NoStore==true,"Completion response contains no rows/connection settings and is not cached");
            var invalid=await client.GetAsync("/Sql?handler=Completions&db=NoSuchCompletionDatabase");
            check(invalid.StatusCode==HttpStatusCode.BadRequest && (await invalid.Content.ReadAsStringAsync()).Contains("unavailable"),"Invalid completion database does not fall back");
            check((await client.GetAsync("/connection.json")).StatusCode==HttpStatusCode.NotFound,"External connection file is not served over HTTP");
            check((await client.GetStringAsync("/Sql?db=AdminerTest")).Contains("Refresh suggestions"),"SQL command editor exposes completion controls");
            check((await client.GetStringAsync($"/Browse?db=AdminerTest&id={table.Id}")).Contains("Refresh suggestions"),"Browse editor exposes the shared completion controls");
            var script=await client.GetStringAsync("/js/sql-completion.js");
            check(script.Contains("AdminerSqlCompletion"),"Editor assets load with executable-based content root");
        }
        finally { await Execute($"DROP VIEW IF EXISTS {view}; DROP TABLE IF EXISTS {qualified};"); Console.WriteLine("Removed only completion metadata fixtures inside AdminerTest."); }
    }
}
