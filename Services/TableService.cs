using System.Data;
using DataRow = AdminerDOTNET.Models.DataRow;
using System.Security.Cryptography;
using System.Text.Json;
using AdminerDOTNET.Models;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.SqlClient;

namespace AdminerDOTNET.Services;

public sealed class TableService(DatabaseConnection database, MetadataService metadata, IDataProtectionProvider protection)
{
    private IDataProtector Protector(string? db) => protection.CreateProtector("AdminerDOTNET.Rows.v2", database.ContextIdentity(db));
    private static string Shape(TableMetadata table) => JsonSerializer.Serialize(table.Columns);
    private static string SelectColumns(TableMetadata table) => string.Join(",", table.Columns.Select(c => SqlNames.Quote(c.Name)));

    private string Protect(TableMetadata table, List<string?> values, string? db) => Protector(db).Protect(JsonSerializer.Serialize(new RowSnapshot(table.Object.Id, Shape(table), values)));

    public RowSnapshot Decode(TableMetadata table, string token, string? db = null)
    {
        try
        {
            var snapshot = JsonSerializer.Deserialize<RowSnapshot>(Protector(db).Unprotect(token));
            if (snapshot is null || snapshot.ObjectId != table.Object.Id || snapshot.Shape != Shape(table) || snapshot.Values.Count != table.Columns.Count)
                throw new InvalidOperationException("The table structure changed. Reload the record before editing.");
            return snapshot;
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException or ArgumentException)
        { throw new InvalidOperationException("This record link belongs to another server/database, is invalid, or has expired. Browse the selected database again."); }
    }

    public Task<BrowseResult> BrowseAsync(int id, int page, int? sort, bool descending, int? filter, string op, string? value, CancellationToken ct, string? db = null)
        => BrowseAsync(id, page, sort, descending, filter.HasValue ? new FilterNode { Column = filter, Operator = op, Value = value ?? "" } : FilterState.Parse(null), ct, db);

    public async Task<BrowseResult> BrowseAsync(int id, long page, int? sort, bool descending, FilterNode filters, CancellationToken ct, string? db = null)
    {
        var table = await metadata.GetAsync(id, ct, db);
        var query = BrowseQuery.Build(table, filters, sort, descending);
        await using var connection = await database.OpenAsync(ct, db);
        using var command = new SqlCommand { Connection = connection, CommandTimeout = 60 };
        command.Parameters.AddRange(query.Parameters.ToArray());
        command.CommandText = $"SELECT COUNT_BIG(*) FROM {table.Object.Qualified};" +
            (query.Where.Length == 0 ? "" : $" SELECT COUNT_BIG(*) FROM {table.Object.Qualified}{query.Where};");
        long total;
        long matching;
        using (var counts = await command.ExecuteReaderAsync(ct))
        {
            await counts.ReadAsync(ct);
            total = counts.GetInt64(0);
            matching = total;
            if (await counts.NextResultAsync(ct))
            {
                await counts.ReadAsync(ct);
                matching = counts.GetInt64(0);
            }
        }
        var pages = matching == 0 ? 1 : (matching - 1) / BrowseResult.PageSize + 1;
        page = Math.Clamp(page, 1, pages);
        var offset = checked((page - 1) * BrowseResult.PageSize);
        command.CommandText = query.PageSql(table);
        command.Parameters.Add("@offset", SqlDbType.BigInt).Value = offset;
        using var reader = await command.ExecuteReaderAsync(ct);
        var rows = new List<DataRow>();
        while (await reader.ReadAsync(ct))
        {
            var values = Enumerable.Range(0, table.Columns.Count).Select(i => SqlValues.Read(reader, i)).ToList();
            rows.Add(new(values, table.CanEdit ? Protect(table, values, db) : null));
        }
        return new(table, rows, total, matching, page, query.Script(command.CommandText, offset));
    }

    private static string Match(SqlCommand command, TableMetadata table, RowSnapshot snapshot)
    {
        var predicates = new List<string>();
        var columns = table.Keys.Count > 0 ? table.Keys : table.Columns;
        foreach (var column in columns)
        {
            var value = snapshot.Values[table.Columns.IndexOf(column)];
            var quoted = SqlNames.Quote(column.Name);
            var name = "@k" + column.Id;
            if (value is null) { predicates.Add(quoted + " IS NULL"); continue; }
            if (column.Type == "xml")
            {
                // SqlClient's XML text serialization can differ lexically (for example <r /> vs <r/>).
                // Normalize both the original parameter and stored value through SQL Server's XML serializer.
                predicates.Add($"CONVERT(varbinary(max), CONVERT(nvarchar(max), {quoted})) = CONVERT(varbinary(max), CONVERT(nvarchar(max), {name}))");
                command.Parameters.Add(name, SqlDbType.Xml).Value = value;
            }
            else if (column.Textual)
            {
                // Binary comparison retains case, trailing spaces, and exact legacy text content.
                predicates.Add($"CONVERT(varbinary(max), CONVERT(nvarchar(max), {quoted})) = {name}");
                command.Parameters.Add(name, SqlDbType.VarBinary, -1).Value = System.Text.Encoding.Unicode.GetBytes(value);
            }
            else if (column.Type == "image")
            {
                predicates.Add($"CONVERT(varbinary(max), {quoted}) = {name}");
                command.Parameters.Add(name, SqlDbType.VarBinary, -1).Value = Convert.FromHexString(value[2..]);
            }
            else
            {
                if (!column.Matchable) throw new InvalidOperationException($"Cannot reliably identify this record using column {column.Name} ({column.Type}). Use the SQL editor.");
                predicates.Add(quoted + "=" + name);
                command.Parameters.Add(SqlValues.Parameter(name, column, value));
            }
        }
        if (predicates.Count == 0) throw new InvalidOperationException("No columns are available to identify this record.");
        return string.Join(" AND ", predicates);
    }

    public async Task<string> RefreshAsync(int id, string token, CancellationToken ct, string? db = null)
    {
        var table = await metadata.GetAsync(id, ct, db);
        var snapshot = Decode(table, token, db);
        if (!table.CanEdit) throw new InvalidOperationException("This table is no longer editable.");
        await using var connection = await database.OpenAsync(ct, db);
        using var command = new SqlCommand { Connection = connection };
        var predicate = Match(command, table, snapshot);
        command.CommandText = $"SELECT TOP (1) {SelectColumns(table)} FROM {table.Object.Qualified} WHERE {predicate}";
        using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) throw new InvalidOperationException("This record was changed or deleted. Return to the table.");
        return Protect(table, Enumerable.Range(0,table.Columns.Count).Select(i => SqlValues.Read(reader,i)).ToList(), db);
    }

    public async Task<string> SaveAsync(int id, string? token, Dictionary<int, FieldInput> fields, bool delete, CancellationToken ct, string? db = null)
    {
        if (delete)
        {
            if (token is null) throw new InvalidOperationException("Select a record before deleting.");
            await DeleteRowsAsync(id, [token], ct, db);
            return "Record deleted.";
        }
        var table = await metadata.GetAsync(id, ct, db);
        var snapshot = token is null ? null : Decode(table, token, db);
        if (snapshot is null ? !table.CanInsert : !table.CanEdit) throw new InvalidOperationException("This object does not support this record operation. Use SQL for views or unsupported matching types.");
        await using var connection = await database.OpenAsync(ct, db);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        using var command = new SqlCommand { Connection = connection, Transaction = transaction, CommandTimeout = 60 };
        var predicate = "";
        if (snapshot is not null)
        {
            predicate = await LockAndCheckAsync(command, table, snapshot, ct);
        }
        var keyless = snapshot is not null && table.Keys.Count == 0;
        var target = keyless ? "[record_target]" : table.Object.Qualified;
        var where = keyless ? "" : " WHERE " + predicate;
        {
            var names = new List<string>();
            var values = new List<string>();
            var sets = new List<string>();
            foreach (var column in table.Columns.Where(c => c.Writable && (snapshot is null || c.KeyOrder == 0)))
            {
                if (!fields.TryGetValue(column.Id, out var field)) throw new InvalidOperationException($"Missing field: {column.Name}.");
                if (field.Mode == "default")
                {
                    if (snapshot is not null) sets.Add(SqlNames.Quote(column.Name) + "=DEFAULT");
                    continue;
                }
                if (field.Mode is not "value" and not "null") throw new InvalidOperationException("Invalid field mode.");
                if (field.Mode == "null" && !column.Nullable) throw new InvalidOperationException($"{column.Name} does not allow NULL.");
                var parameter = "@v" + column.Id;
                command.Parameters.Add(SqlValues.Parameter(parameter, column, field.Mode == "null" ? null : field.Value ?? ""));
                names.Add(SqlNames.Quote(column.Name));
                values.Add(parameter);
                sets.Add(SqlNames.Quote(column.Name) + "=" + parameter);
            }
            command.CommandText = snapshot is null
                ? names.Count == 0 ? $"INSERT INTO {table.Object.Qualified} DEFAULT VALUES" : $"INSERT INTO {table.Object.Qualified} ({string.Join(",",names)}) VALUES ({string.Join(",",values)})"
                : sets.Count == 0 ? throw new InvalidOperationException("There are no editable non-key columns.") : $"UPDATE {target} SET {string.Join(",",sets)}{where}";
        }
        if (keyless) command.CommandText = $"WITH [record_target] AS (SELECT TOP (1) * FROM {table.Object.Qualified} WITH (UPDLOCK,HOLDLOCK) WHERE {predicate}) " + command.CommandText;
        await ExecuteOneAsync(command, ct);
        await transaction.CommitAsync(ct);
        return snapshot is null ? "Record inserted." : "Record saved.";
    }

    public async Task<int> DeleteRowsAsync(int id, IReadOnlyList<string> tokens, CancellationToken ct, string? db = null)
    {
        if (tokens.Count is < 1 or > BrowseResult.PageSize) throw new InvalidOperationException("Select between 1 and 100 rows on the current page.");
        if (tokens.Any(string.IsNullOrWhiteSpace) || tokens.Distinct(StringComparer.Ordinal).Count() != tokens.Count)
            throw new InvalidOperationException("The selection contains empty or repeated row tokens. Reload the table and select rows again.");
        var table = await metadata.GetAsync(id, ct, db);
        if (!table.CanEdit) throw new InvalidOperationException("This table does not support record deletion. Views and unsupported matching types cannot be deleted through Browse.");
        var snapshots = tokens.Select(token => Decode(table, token, db)).ToList();
        if (table.Keys.Count > 0)
        {
            var identities = snapshots.Select(snapshot => JsonSerializer.Serialize(table.Keys.Select(key => snapshot.Values[table.Columns.IndexOf(key)])));
            if (identities.Distinct(StringComparer.Ordinal).Count() != snapshots.Count)
                throw new InvalidOperationException("The selection contains the same primary key more than once. Reload the table and select rows again.");
        }
        await using var connection = await database.OpenAsync(ct, db);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        foreach (var snapshot in snapshots)
        {
            using var command = new SqlCommand { Connection = connection, Transaction = transaction, CommandTimeout = 60 };
            var predicate = await LockAndCheckAsync(command, table, snapshot, ct);
            command.CommandText = table.Keys.Count == 0
                ? $"WITH [record_target] AS (SELECT TOP (1) * FROM {table.Object.Qualified} WITH (UPDLOCK,HOLDLOCK) WHERE {predicate}) DELETE FROM [record_target]"
                : $"DELETE FROM {table.Object.Qualified} WHERE {predicate}";
            await ExecuteOneAsync(command, ct);
        }
        await transaction.CommitAsync(ct);
        return snapshots.Count;
    }

    private static async Task<string> LockAndCheckAsync(SqlCommand command, TableMetadata table, RowSnapshot snapshot, CancellationToken ct)
    {
        var predicate = Match(command, table, snapshot);
        command.CommandText = $"SELECT TOP (1) {SelectColumns(table)} FROM {table.Object.Qualified} WITH (UPDLOCK,HOLDLOCK) WHERE {predicate}";
        using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) throw new InvalidOperationException("This record was changed or deleted. Reload the table.");
        var version = table.Columns.FirstOrDefault(c => c.IsVersion);
        var compare = version is null ? table.Columns.Where(c => c.Writable || c.KeyOrder > 0) : [version];
        foreach (var column in compare)
        {
            var index = table.Columns.IndexOf(column);
            if (!string.Equals(SqlValues.Read(reader, index), snapshot.Values[index], StringComparison.Ordinal))
                throw new InvalidOperationException("This record changed since you opened it. Reload it before saving or deleting.");
        }
        return predicate;
    }

    private static async Task ExecuteOneAsync(SqlCommand command, CancellationToken ct)
    {
        // @@ROWCOUNT captures the target statement, excluding row counts emitted by triggers.
        command.CommandText += "; SELECT CAST(@@ROWCOUNT AS int);";
        using (var reader = await command.ExecuteReaderAsync(ct))
        {
            int? affected = null;
            do
            {
                while (await reader.ReadAsync(ct))
                    if (reader.FieldCount == 1 && reader.GetFieldType(0) == typeof(int)) affected = reader.GetInt32(0);
            } while (await reader.NextResultAsync(ct));
            if (affected != 1) throw new InvalidOperationException("Expected exactly one changed row. The transaction was rolled back.");
        }
    }
}
