using System.IO.Compression;
using System.Text;
using Microsoft.Data.SqlClient;
using AdminerDOTNET.Models;

namespace AdminerDOTNET.Services;

public sealed class CsvArchive(Stream stream) : IDisposable
{
    private readonly ZipArchive zip = new(stream, ZipArchiveMode.Create, leaveOpen: true);
    public async Task<ExportSet> WriteAsync(SqlDataReader reader, int number, CancellationToken ct)
    {
        var entry = zip.CreateEntry($"result-{number:000}.csv", CompressionLevel.Fastest);
        await using var output = entry.Open();
        await using var writer = new StreamWriter(output, new UTF8Encoding(true), 65536, leaveOpen: true) { NewLine = "\r\n" };
        for (var i = 0; i < reader.FieldCount; i++)
        {
            if (i != 0) await writer.WriteAsync(",".AsMemory(), ct);
            await FieldAsync(writer, reader.GetName(i), ct);
        }
        await writer.WriteLineAsync(ReadOnlyMemory<char>.Empty, ct);
        long rows = 0;
        while (await reader.ReadAsync(ct))
        {
            for (var i = 0; i < reader.FieldCount; i++)
            {
                if (i != 0) await writer.WriteAsync(",".AsMemory(), ct);
                await FieldAsync(writer, SqlValues.Read(reader, i), ct);
            }
            await writer.WriteLineAsync(ReadOnlyMemory<char>.Empty, ct);
            rows++;
        }
        await writer.FlushAsync(ct);
        return new(number, rows);
    }
    public static async Task FieldAsync(TextWriter writer, string? value, CancellationToken ct)
    {
        if (value is null) return;
        if (value.Length != 0 && value.AsSpan().IndexOfAny(",\"\r\n") < 0) { await writer.WriteAsync(value.AsMemory(), ct); return; }
        await writer.WriteAsync("\"".AsMemory(), ct);
        // Write segments rather than making another full copy of a potentially large cell.
        var start = 0;
        for (var i = 0; i < value.Length; i++)
            if (value[i] == '"')
            {
                await writer.WriteAsync(value.AsMemory(start, i - start), ct);
                await writer.WriteAsync("\"\"".AsMemory(), ct);
                start = i + 1;
            }
        await writer.WriteAsync(value.AsMemory(start), ct);
        await writer.WriteAsync("\"".AsMemory(), ct);
    }
    public void Dispose() => zip.Dispose();
}
