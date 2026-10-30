namespace AdminerDOTNET.Models;

public record DbObject(int Id, string Schema, string Name, bool IsView)
{
    public string Qualified => $"{SqlNames.Quote(Schema)}.{SqlNames.Quote(Name)}";
    public string Label => $"{Schema}.{Name}";
}

public static class SqlNames
{
    public static string Quote(string name) => "[" + name.Replace("]", "]]") + "]";
}

public record Column(int Id, string Name, string Type, short Length, byte Precision, byte Scale,
    bool Nullable, bool Identity, bool Computed, string? Default, int KeyOrder, bool Generated)
{
    public bool IsVersion => Type is "timestamp" or "rowversion";
    public bool Textual => Type is "char" or "varchar" or "nchar" or "nvarchar" or "text" or "ntext";
    public bool Matchable => Supported || IsVersion || Type is "image" or "xml";
    public bool Supported => Type is "bigint" or "int" or "smallint" or "tinyint" or "bit"
        or "numeric" or "decimal" or "money" or "smallmoney" or "float" or "real"
        or "char" or "varchar" or "nchar" or "nvarchar" or "text" or "ntext"
        or "uniqueidentifier" or "date" or "datetime" or "datetime2" or "smalldatetime"
        or "datetimeoffset" or "time" or "binary" or "varbinary";
    public bool Writable => Supported && !Identity && !Computed && !IsVersion && !Generated;
    public string DisplayType => Type switch
    {
        "nvarchar" or "nchar" => $"{Type}({(Length == -1 ? "max" : Length / 2)})",
        "varchar" or "char" or "binary" or "varbinary" => $"{Type}({(Length == -1 ? "max" : Length)})",
        "numeric" or "decimal" => $"{Type}({Precision},{Scale})",
        "datetime2" or "datetimeoffset" or "time" => $"{Type}({Scale})",
        _ => Type
    };
}

public record TableMetadata(DbObject Object, List<Column> Columns, List<string> Indexes, List<string> ForeignKeys)
{
    public List<Column> Keys => Columns.Where(c => c.KeyOrder > 0).OrderBy(c => c.KeyOrder).ToList();
    public bool CanInsert => !Object.IsView;
    public bool CanEdit => !Object.IsView && (Keys.Count > 0 ? Keys.All(c => c.Supported) : Columns.All(c => c.Matchable));
}

public record DataRow(List<string?> Values, string? Token = null);
public record BrowseResult(TableMetadata Metadata, List<DataRow> Rows, long TotalRows, long MatchingRows, long Page, string Sql)
{
    public const int PageSize = 100;
    public long TotalPages => MatchingRows == 0 ? 0 : (MatchingRows - 1) / PageSize + 1;
    public bool HasNext => Page < TotalPages;
    public long FirstRow => Rows.Count == 0 ? 0 : (Page - 1) * PageSize + 1;
    public long LastRow => Rows.Count == 0 ? 0 : FirstRow + Rows.Count - 1;
}
public record RowSnapshot(int ObjectId, string Shape, List<string?> Values);
public record FieldInput(string Mode, string? Value);
public record ResultGrid(List<string> Columns, List<List<string?>> Rows, bool Truncated);
public class QueryResult
{
    public List<ResultGrid> Grids { get; } = [];
    public List<string> Messages { get; } = [];
    public string? Error { get; set; }
    public int Affected { get; set; } = -1;
    public double Milliseconds { get; set; }
}
