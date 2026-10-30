using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AdminerDOTNET.Services;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;

public static class ExportChecks
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }
    public static async Task RunAsync(IConfiguration config, Action<bool,string> check, bool timeout)
    {
        var database = new DatabaseConnection(config);
        var queries = new QueryService(database);
        var root = Path.Combine(Path.GetTempPath(), "AdminerExportChecks_" + Guid.NewGuid().ToString("N"));
        var clock = new Clock();
        using var store = new CsvExportStore(root, clock);
        await using var connection = await database.OpenAsync(default, "AdminerTest");
        using (var guard = new SqlCommand("SELECT DB_NAME()", connection))
            check((string)(await guard.ExecuteScalarAsync())! == "AdminerTest", "CSV fixtures are confined to AdminerTest");
        var name = "[dbo].[AdminerExport_" + Guid.NewGuid().ToString("N")[..10] + "]";
        async Task Execute(string sql) { using var cmd = new SqlCommand(sql, connection); await cmd.ExecuteNonQueryAsync(); }
        Task<AdminerDOTNET.Models.ExportResult> Export(string sql, Guid? id = null, CancellationToken ct = default)
            => queries.ExportAsync(id ?? Guid.NewGuid(), sql, store, ct, "AdminerTest");
        static async Task<string> Read(ZipArchiveEntry entry)
        {
            using var reader = new StreamReader(entry.Open(), Encoding.UTF8, true);
            return await reader.ReadToEndAsync();
        }
        try
        {
            await Execute($"CREATE TABLE {name}(Id int NOT NULL);");
            var result = await Export($"""
                INSERT INTO {name} VALUES(1);
                SELECT TOP(1505) ROW_NUMBER() OVER(ORDER BY (SELECT NULL)) AS N, N'α,"雪' AS X,
                    CAST(12345678901234567890123456789012345678 AS decimal(38,0)) AS D,
                    CAST('2026-01-02' AS date) AS Day, 0x00FF AS Bin, CAST(1 AS bit) AS BitValue,
                    N'' AS EmptyText, CAST(NULL AS nvarchar(2)) AS NullText
                FROM sys.all_columns;
                SELECT N'a'+CHAR(13)+CHAR(10)+N'b' AS [a,b], N'"q"' AS Same, N'' AS Same, NULL;
                SELECT 1 AS EmptySet WHERE 1=0;
                PRINT N'export message';
                """);
            check(result.Error is null && result.Token is not null && result.Sets.Count == 3 && result.Sets[0].Rows == 1505 && result.Sets[2].Rows == 0,
                "CSV export bypasses display row/cell caps and includes empty/multiple result sets");
            check(result.Affected == 1 && result.Messages.Contains("export message"), "Export retains affected counts and messages");
            using (var stream = store.Open(result.Token!, database.ContextIdentity("AdminerTest"))!)
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Read))
            {
                check(zip.Entries.Select(e => e.FullName).SequenceEqual(new[] { "result-001.csv", "result-002.csv", "result-003.csv" }), "ZIP contains ordered CSV entries");
                using (var entry = zip.Entries[0].Open())
                {
                    var bom = new byte[3]; await entry.ReadExactlyAsync(bom);
                    check(bom.SequenceEqual(new byte[] { 239, 187, 191 }), "CSV has UTF-8 BOM");
                }
                var first = await Read(zip.Entries[0]);
                check(first.Split("\r\n").Length == 1507 && first.Contains("1505,"), "CSV contains every exported row with CRLF records");
                check(first.Contains("\"α,\"\"雪\"") && first.Contains("12345678901234567890123456789012345678") && first.Contains("2026-01-02T00:00:00") && first.Contains("0x00FF,1,\"\",\r\n"),
                    "CSV preserves Unicode, escaping, precision, dates, binary, bits and NULL/empty distinctions");
                var second = await Read(zip.Entries[1]);
                check(second == "\"a,b\",Same,Same,\"\"\r\n\"a\r\nb\",\"\"\"q\"\"\",\"\",\r\n", "CSV preserves duplicate/unnamed headers and embedded newlines/quotes");
                check(await Read(zip.Entries[2]) == "EmptySet\r\n", "Empty results produce header-only CSV");
            }
            using (var count = new SqlCommand($"SELECT COUNT_BIG(*) FROM {name}", connection))
                check((long)(await count.ExecuteScalarAsync())! == 1, "Execute & export executes mutation exactly once");
            using (var retry = store.Open(result.Token!, database.ContextIdentity("AdminerTest"))) check(retry is not null, "Completed export permits retry downloads");
            check(store.Open(result.Token!, database.ContextIdentity("SimulatedOtherDatabase")) is null && store.Open("../invalid", "x") is null, "Export rejects foreign database and malformed tokens without opening another database");
            var alternate = new DatabaseConnection(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> { ["ConnectionStrings:DefaultConnection"]="Server=(localdb)\\NeverOpened;Database=AdminerTest;Integrated Security=true" }).Build());
            check(store.Open(result.Token!, alternate.ContextIdentity("AdminerTest")) is null, "Export rejects foreign configured server context");
            var error = await Export("SELECT 1; THROW 50000, 'export failure', 1;");
            check(error.Error?.Contains("export failure") == true && error.Token is null, "SQL error after a result exposes no partial download");
            check((await Export("GO")).Error?.Contains("GO") == true && (await Export(" ")).Error is not null, "Export rejects GO and empty SQL");
            var none = await Export($"INSERT INTO {name} VALUES(2);");
            check(none.Error is null && none.Token is null && none.Sets.Count == 0 && none.Affected == 1, "No-result batches report effects without ZIP");
            var many = await Export(string.Concat(Enumerable.Repeat("SELECT 1;", 102)));
            check(many.Error is null && many.Sets.Count == 102, "Export has no display result-set cap");
            var id = Guid.NewGuid();
            var running = Export("WAITFOR DELAY '00:00:10'; SELECT 1", id);
            var cancelled = false;
            for (var i=0; i<50 && !cancelled; i++) { cancelled = queries.Cancel(id, "AdminerTest"); if (!cancelled) await Task.Delay(20); }
            var stopped = await running;
            check(cancelled && stopped.Error?.Contains("cancel", StringComparison.OrdinalIgnoreCase) == true && stopped.Token is null, "Existing cancellation registry cancels export and removes partial output");
            using (var abort = new CancellationTokenSource(150))
                check((await Export("SELECT 1; WAITFOR DELAY '00:00:10';", ct:abort.Token)).Error is not null, "Request abortion cancels export after earlier results");
            check(!Directory.EnumerateFiles(root, "*.partial", SearchOption.AllDirectories).Any(), "Failed, cancelled and no-result exports leave no partial files");
            clock.Now = clock.Now.AddMinutes(11);
            check(store.Open(result.Token!, database.ContextIdentity("AdminerTest")) is null, "Download expires after ten minutes");
            store.Sweep();
            check(!Directory.EnumerateFiles(root, "*.zip", SearchOption.AllDirectories).Any(), "Expiry sweep removes completed files");
            var orphan = Path.Combine(root, "run-abandoned"); Directory.CreateDirectory(orphan);
            await File.WriteAllTextAsync(Path.Combine(orphan,"lease"), "");
            await File.WriteAllTextAsync(Path.Combine(orphan,"abandoned.partial"), "incomplete");
            using (var other = new CsvExportStore(root))
            {
                await using var pending = other.Create();
                store.Sweep();
                check(!Directory.Exists(orphan) && File.Exists(pending.Stream.Name), "Startup sweep removes abandoned files but preserves another active exporter");
            }
            var blocked = Path.Combine(root,"blocked"); await File.WriteAllTextAsync(blocked,"file");
            using (var broken = new CsvExportStore(blocked))
            {
                var failed = await queries.ExportAsync(Guid.NewGuid(), "SELECT 1", broken, default, "AdminerTest");
                check(failed.Error?.Contains("disk space") == true && failed.Token is null && !failed.Error.Contains(blocked), "Storage failure is actionable and exposes no path or partial token");
            }
            if (timeout)
            {
                var timed = await Export("WAITFOR DELAY '00:01:05'; SELECT 1;");
                check(timed.Error?.Contains("60-second") == true && timed.Token is null, "Export enforces real 60-second SQL timeout");
            }
            using var client = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:5186") };
            client.DefaultRequestHeaders.Add("Origin", "http://127.0.0.1:5186");
            var html = await client.GetStringAsync("/Sql?db=AdminerTest");
            var csrf = WebUtility.HtmlDecode(Regex.Match(html,"name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value);
            async Task<JsonElement> HttpExport(string path, string sql, int? objectId=null)
            {
                var fields = new Dictionary<string,string> { ["db"]="AdminerTest", ["CommandText"]=sql, ["ExecutionId"]=Guid.NewGuid().ToString(), ["__RequestVerificationToken"]=csrf };
                if (objectId.HasValue) fields["Id"]=objectId.Value.ToString();
                var response = await client.PostAsync(path, new FormUrlEncodedContent(fields));
                check(response.IsSuccessStatusCode, "Protected HTTP export accepts same-origin form");
                return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
            }
            var http = await HttpExport("/Sql?handler=Export", "SELECT TOP(1300) ROW_NUMBER() OVER(ORDER BY (SELECT NULL)) AS N FROM sys.all_columns;");
            var url = http.GetProperty("downloadUrl").GetString()!;
            using (var response = await client.GetAsync(url))
            {
                check(response.Content.Headers.ContentType?.MediaType == "application/zip" && response.Content.Headers.ContentDisposition?.DispositionType == "attachment", "HTTP download streams ZIP attachment");
                using var zip = new ZipArchive(await response.Content.ReadAsStreamAsync(), ZipArchiveMode.Read);
                check((await Read(zip.Entries.Single())).Contains("1300\r\n"), "HTTP export downloads beyond displayed limit");
            }
            check((await client.GetAsync(url)).IsSuccessStatusCode, "HTTP download may be retried without executing SQL");
            check((await client.GetAsync("/Sql?handler=Download&db=AdminerTest&token=invalid")).StatusCode==HttpStatusCode.NotFound, "HTTP rejects invalid download token");
            check((await client.GetAsync(url.Replace("db=AdminerTest","db=NoSuchExportDatabase"))).StatusCode==HttpStatusCode.BadRequest, "HTTP rejects invalid selected database without fallback");
            using var objectCommand = new SqlCommand($"SELECT OBJECT_ID(N'{name}')", connection);
            var oid = (int)(await objectCommand.ExecuteScalarAsync())!;
            var browse = await HttpExport("/Browse?handler=Export", "SELECT 7 AS BrowseCsv", oid);
            check(browse.GetProperty("downloadUrl").GetString() is not null && (await client.GetStringAsync($"/Browse?db=AdminerTest&id={oid}")).Contains("Execute &amp; export CSV"), "Browse exposes shared export and preserves independent view");
            check((await client.PostAsync("/Sql?handler=Export",new FormUrlEncodedContent([]))).StatusCode==HttpStatusCode.BadRequest &&
                (await client.PostAsync("/Browse?handler=Export",new FormUrlEncodedContent([]))).StatusCode==HttpStatusCode.BadRequest, "Both export handlers enforce antiforgery");
            using var cross = new HttpRequestMessage(HttpMethod.Post,"/Sql?handler=Export") { Content=new FormUrlEncodedContent(new Dictionary<string,string>{["__RequestVerificationToken"]=csrf}) };
            cross.Headers.Add("Origin","https://example.com");
            check((await client.SendAsync(cross)).StatusCode==HttpStatusCode.Forbidden, "Export rejects unrelated cross-origin POST");
        }
        finally
        {
            await Execute($"DROP TABLE IF EXISTS {name};");
            store.Dispose();
            // Only the uniquely named test root is removed.
            if (Path.GetFullPath(root).StartsWith(Path.GetFullPath(Path.GetTempPath()),StringComparison.OrdinalIgnoreCase)) Directory.Delete(root,true);
            Console.WriteLine("Removed only CSV fixtures inside AdminerTest and their private temporary export files.");
        }
    }
}
