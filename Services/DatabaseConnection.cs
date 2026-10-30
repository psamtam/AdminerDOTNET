using Microsoft.Data.SqlClient;
using System.Security.Cryptography;
using System.Text;

namespace AdminerDOTNET.Services;

public class DatabaseConnection(IConfiguration configuration)
{
    private readonly string? connectionString = configuration.GetConnectionString("DefaultConnection");
    public SqlConnectionStringBuilder Settings(string? db = null)
    {
        var value = connectionString;
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException("Set ConnectionStrings:DefaultConnection in connection.json beside the EXE (or Development User Secrets), then restart the app.");
        SqlConnectionStringBuilder settings;
        try { settings = new(value); }
        catch (ArgumentException) { throw new InvalidOperationException("ConnectionStrings:DefaultConnection is not a valid SQL Server connection string. Check connection.json or your configuration override and restart the app."); }
        if (string.IsNullOrWhiteSpace(settings.InitialCatalog))
            throw new InvalidOperationException("Specify Database in ConnectionStrings:DefaultConnection. This app does not select a default server database.");
        if (db is not null) settings.InitialCatalog = db;
        return settings;
    }

    // Only immutable connection settings are shared; the selected database belongs to each operation.
    public string ContextIdentity(string? db = null) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(Settings(db).ConnectionString)));

    public virtual async Task<List<string>> DatabasesAsync(CancellationToken cancellation = default)
    {
        await using var connection = await OpenConnectionAsync(Settings(), cancellation);
        using var command = new SqlCommand("""
            SELECT name FROM sys.databases
            WHERE database_id > 4 AND state = 0 AND HAS_DBACCESS(name) = 1
            ORDER BY name;
            """, connection);
        using var reader = await command.ExecuteReaderAsync(cancellation);
        var names = new List<string>();
        while (await reader.ReadAsync(cancellation)) names.Add(reader.GetString(0));
        return names;
    }

    public string Resolve(string? db, IReadOnlyList<string> available)
    {
        var selected = db ?? Settings().InitialCatalog;
        return available.FirstOrDefault(name => string.Equals(name, selected, StringComparison.Ordinal))
            ?? throw new InvalidOperationException("The selected database is unavailable or inaccessible. Choose an accessible online user database, or check the configured default and retry. No command was run against a different database.");
    }

    public async Task<SqlConnection> OpenAsync(CancellationToken cancellation = default, string? db = null)
    {
        if (db is not null) db = Resolve(db, await DatabasesAsync(cancellation));
        return await OpenConnectionAsync(Settings(db), cancellation);
    }

    protected virtual async Task<SqlConnection> OpenConnectionAsync(SqlConnectionStringBuilder settings, CancellationToken cancellation)
    {
        var connection = new SqlConnection(settings.ConnectionString);
        try
        {
            await connection.OpenAsync(cancellation);
            return connection;
        }
        catch (OperationCanceledException) { await connection.DisposeAsync(); throw; }
        catch (Exception ex) when (ex is SqlException or InvalidOperationException)
        {
            await connection.DisposeAsync();
            throw new InvalidOperationException("Cannot connect to the selected database. Check database availability, your Windows account, and connection.json beside the EXE (or its override), then restart after configuration changes. Database discovery also requires the configured default to be accessible. If using an explicit pipe, its address may have changed after a restart.");
        }
    }

    public string DatabaseName
    {
        get
        {
            try { return string.IsNullOrWhiteSpace(connectionString) ? "Not configured" : new SqlConnectionStringBuilder(connectionString).InitialCatalog; }
            catch { return "Not configured"; }
        }
    }
}
