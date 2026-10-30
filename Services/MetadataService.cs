using AdminerDOTNET.Models;
using Microsoft.Data.SqlClient;

namespace AdminerDOTNET.Services;

public sealed class MetadataService(DatabaseConnection database)
{
    public async Task<CompletionMetadata> CompletionsAsync(CancellationToken ct, string? db = null)
    {
        await using var connection = await database.OpenAsync(ct, db);
        using var command = new SqlCommand("""
            SELECT o.object_id, SCHEMA_NAME(o.schema_id), o.name, o.type, c.name,
                COALESCE(bt.name,t.name), c.max_length, c.precision, c.scale
            FROM sys.objects o JOIN sys.columns c ON c.object_id=o.object_id
            JOIN sys.types t ON t.user_type_id=c.user_type_id
            LEFT JOIN sys.types bt ON bt.user_type_id=c.system_type_id AND bt.user_type_id=bt.system_type_id
            WHERE o.type IN ('U','V') AND o.is_ms_shipped=0
            ORDER BY o.schema_id,o.name,c.column_id;
            SELECT COALESCE(SCHEMA_NAME(),N'dbo');
            """,connection);
        using var reader = await command.ExecuteReaderAsync(ct);
        var objects = new List<CompletionObject>();
        CompletionObject? current = null;
        while (await reader.ReadAsync(ct))
        {
            var id = reader.GetInt32(0);
            if (current?.Id != id)
            {
                current = new(id,reader.GetString(1),reader.GetString(2),reader.GetString(3).Trim()=="V",[]);
                objects.Add(current);
            }
            var type = new Column(0,reader.GetString(4),reader.GetString(5),reader.GetInt16(6),reader.GetByte(7),reader.GetByte(8),false,false,false,null,0,false);
            current.Columns.Add(new(type.Name,type.DisplayType));
        }
        var defaultSchema = "dbo";
        if (await reader.NextResultAsync(ct) && await reader.ReadAsync(ct)) defaultSchema = reader.GetString(0);
        return new(db ?? connection.Database,objects,defaultSchema);
    }

    public async Task<List<DbObject>> ObjectsAsync(CancellationToken ct, string? db = null)
    {
        await using var connection = await database.OpenAsync(ct, db);
        using var command = new SqlCommand("SELECT object_id, SCHEMA_NAME(schema_id), name, type FROM sys.objects WHERE type IN ('U','V') AND is_ms_shipped=0 ORDER BY schema_id, name", connection);
        using var reader = await command.ExecuteReaderAsync(ct);
        var objects = new List<DbObject>();
        while (await reader.ReadAsync(ct)) objects.Add(new(reader.GetInt32(0), reader.GetString(1), reader.GetString(2), reader.GetString(3).Trim() == "V"));
        return objects;
    }

    public async Task<TableMetadata> GetAsync(int id, CancellationToken ct, string? db = null)
    {
        var obj = (await ObjectsAsync(ct, db)).SingleOrDefault(o => o.Id == id)
            ?? throw new InvalidOperationException("That table or view no longer exists in the selected database.");
        await using var connection = await database.OpenAsync(ct, db);
        using var command = new SqlCommand("""
            SELECT c.column_id, c.name, COALESCE(bt.name,t.name), c.max_length, c.precision, c.scale,
                c.is_nullable, c.is_identity, c.is_computed, d.definition,
                ISNULL(pk.key_ordinal,0), c.generated_always_type
            FROM sys.columns c
            JOIN sys.types t ON t.user_type_id=c.user_type_id
            LEFT JOIN sys.types bt ON bt.user_type_id=c.system_type_id AND bt.user_type_id=bt.system_type_id
            LEFT JOIN sys.default_constraints d ON c.default_object_id=d.object_id
            LEFT JOIN (SELECT ic.object_id,ic.column_id,ic.key_ordinal FROM sys.index_columns ic
                JOIN sys.indexes i ON i.object_id=ic.object_id AND i.index_id=ic.index_id WHERE i.is_primary_key=1) pk
                ON pk.object_id=c.object_id AND pk.column_id=c.column_id
            WHERE c.object_id=@id ORDER BY c.column_id;
            SELECT i.name, i.type_desc, i.is_unique, i.is_primary_key, c.name, ic.is_included_column, ic.is_descending_key
            FROM sys.indexes i JOIN sys.index_columns ic ON ic.object_id=i.object_id AND ic.index_id=i.index_id
            JOIN sys.columns c ON c.object_id=ic.object_id AND c.column_id=ic.column_id
            WHERE i.object_id=@id AND i.name IS NOT NULL ORDER BY i.index_id,ic.key_ordinal,ic.index_column_id;
            SELECT fk.name, pc.name, OBJECT_SCHEMA_NAME(fk.referenced_object_id), OBJECT_NAME(fk.referenced_object_id), rc.name,
                fk.delete_referential_action_desc,fk.update_referential_action_desc
            FROM sys.foreign_keys fk JOIN sys.foreign_key_columns fkc ON fkc.constraint_object_id=fk.object_id
            JOIN sys.columns pc ON pc.object_id=fkc.parent_object_id AND pc.column_id=fkc.parent_column_id
            JOIN sys.columns rc ON rc.object_id=fkc.referenced_object_id AND rc.column_id=fkc.referenced_column_id
            WHERE fk.parent_object_id=@id ORDER BY fk.name,fkc.constraint_column_id;
            """, connection);
        command.Parameters.AddWithValue("@id", id);
        using var reader = await command.ExecuteReaderAsync(ct);
        var columns = new List<Column>();
        while (await reader.ReadAsync(ct)) columns.Add(new(reader.GetInt32(0),reader.GetString(1),reader.GetString(2),reader.GetInt16(3),reader.GetByte(4),reader.GetByte(5),reader.GetBoolean(6),reader.GetBoolean(7),reader.GetBoolean(8),reader.IsDBNull(9)?null:reader.GetString(9),Convert.ToInt32(reader[10]),reader.GetByte(11)!=0));
        await reader.NextResultAsync(ct);
        var indexes = new List<string>();
        while (await reader.ReadAsync(ct)) indexes.Add($"{reader[0]} · {reader[1]}{(reader.GetBoolean(2)?" · UNIQUE":"")}{(reader.GetBoolean(3)?" · PRIMARY KEY":"")} · {reader[4]}{(reader.GetBoolean(5)?" (included)":reader.GetBoolean(6)?" DESC":" ASC")}");
        await reader.NextResultAsync(ct);
        var foreignKeys = new List<string>();
        while (await reader.ReadAsync(ct)) foreignKeys.Add($"{reader[0]}: {reader[1]} → {reader[2]}.{reader[3]}.{reader[4]} · DELETE {reader[5]} · UPDATE {reader[6]}");
        return new(obj, columns, indexes, foreignKeys);
    }
}
