using System.Security.Cryptography;

namespace AdminerDOTNET.Services;

// Files never live under the web root. Tokens, not paths, are exposed to the browser.
public sealed class CsvExportStore : BackgroundService
{
    private sealed record Download(string Path, string Context, DateTimeOffset Expires);
    private readonly Dictionary<string, Download> files = new(StringComparer.Ordinal);
    private readonly object gate = new();
    private readonly string root;
    private readonly string directory;
    private readonly TimeProvider clock;
    private FileStream? lease;
    public CsvExportStore() : this(Path.Combine(Path.GetTempPath(), "AdminerDOTNET-exports")) { }
    public CsvExportStore(string root, TimeProvider? clock = null)
    {
        this.root = Path.GetFullPath(root);
        directory = Path.Combine(this.root, "run-" + Guid.NewGuid().ToString("N"));
        this.clock = clock ?? TimeProvider.System;
    }

    public sealed class Pending : IAsyncDisposable
    {
        internal string? Path { get; set; }
        public FileStream Stream { get; }
        internal Pending(string path) { Path = path; Stream = new(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true); }
        public async ValueTask DisposeAsync()
        {
            try { await Stream.DisposeAsync(); }
            finally { if (Path is not null) TryDelete(Path); }
        }
    }

    public Pending Create()
    {
        lock (gate)
        {
            Directory.CreateDirectory(directory);
            lease ??= new(Path.Combine(directory, "lease"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            return new(Path.Combine(directory, Guid.NewGuid().ToString("N") + ".partial"));
        }
    }

    public async Task<string> PublishAsync(Pending pending, string context, CancellationToken ct)
    {
        await pending.Stream.DisposeAsync();
        ct.ThrowIfCancellationRequested();
        lock (gate)
        {
            var path = Path.ChangeExtension(pending.Path!, ".zip");
            File.Move(pending.Path!, path);
            var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            files.Add(token, new(path, context, clock.GetUtcNow().AddMinutes(10)));
            pending.Path = null;
            return token;
        }
    }

    public FileStream? Open(string token, string context)
    {
        lock (gate)
        {
            if (!files.TryGetValue(token, out var file) || file.Context != context || file.Expires <= clock.GetUtcNow()) return null;
            try { return new(file.Path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 65536, true); }
            catch (IOException) { return null; }
        }
    }
    public void Discard(string token)
    {
        lock (gate)
            if (files.TryGetValue(token, out var file))
            {
                if (TryDelete(file.Path)) files.Remove(token);
                else files[token] = file with { Expires = clock.GetUtcNow() };
            }
    }

    public void Sweep()
    {
        lock (gate)
        {
            foreach (var item in files.Where(item => item.Value.Expires <= clock.GetUtcNow()).ToList())
                if (TryDelete(item.Value.Path)) files.Remove(item.Key);
            if (!Directory.Exists(root)) return;
            foreach (var candidate in Directory.EnumerateDirectories(root, "run-*"))
            {
                if (candidate == directory || (File.GetAttributes(candidate) & FileAttributes.ReparsePoint) != 0) continue;
                try
                {
                    // A live process holds this exclusively, protecting even long-running exports.
                    using (new FileStream(Path.Combine(candidate, "lease"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None)) { }
                    foreach (var path in Directory.EnumerateFiles(candidate))
                        if (Path.GetFileName(path) == "lease" || Path.GetExtension(path) is ".zip" or ".partial") TryDelete(path);
                    Directory.Delete(candidate); // Never recursively remove unrelated files.
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
    }
    private static bool TryDelete(string path)
    {
        try { File.Delete(path); return true; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        do
        {
            try { Sweep(); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
    public override void Dispose()
    {
        base.Dispose();
        lock (gate)
        {
            foreach (var file in files.Values) TryDelete(file.Path);
            files.Clear();
            lease?.Dispose();
            TryDelete(Path.Combine(directory, "lease"));
            try { if (Directory.Exists(directory)) Directory.Delete(directory); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
}
