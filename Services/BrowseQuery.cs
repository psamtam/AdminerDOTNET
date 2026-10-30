using System.Data;
using System.Globalization;
using AdminerDOTNET.Models;
using Microsoft.Data.SqlClient;

namespace AdminerDOTNET.Services;

// One query definition supplies both executable parameters and the editable SQL script.
public sealed record BrowseQuery(string Where, string Order, List<SqlParameter> Parameters)
{
    public string PageSql(TableMetadata table) => $"SELECT {string.Join(", ", table.Columns.Select(c => SqlNames.Quote(c.Name)))}\nFROM {table.Object.Qualified}{Where}\nORDER BY {Order}\nOFFSET @offset ROWS FETCH NEXT 100 ROWS ONLY;";
    public string Script(string commandText, long offset) => string.Join("\n", Parameters.Select(SqlScript.Declare)) +
        (Parameters.Count == 0 ? "" : "\n") + $"DECLARE @offset bigint = {offset};\n\n" + commandText;

    public static BrowseQuery Build(TableMetadata table, FilterNode root, int? sort, bool descending)
    {
        var parameters = new List<SqlParameter>();
        var nodes = 0;
        string Compile(FilterNode node, int depth)
        {
            if (node is null) throw new InvalidOperationException("Filter conditions cannot be null.");
            if (++nodes > 50 || depth > 8) throw new InvalidOperationException("Use at most 50 filter nodes and 8 levels of nesting.");
            if (node.Logic is not null || node.Children is not null)
            {
                if (node.Logic is not "and" and not "or" || node.Children is null || node.Column is not null || node.Operator is not null || node.Value is not null)
                    throw new InvalidOperationException("Invalid filter group. Choose AND or OR and valid conditions.");
                if (node.Children.Count == 0)
                {
                    if (depth != 0) throw new InvalidOperationException("Remove empty filter groups or add a condition.");
                    return "";
                }
                return "(" + string.Join(node.Logic == "and" ? " AND " : " OR ", node.Children.Select(child => Compile(child, depth + 1))) + ")";
            }
            var column = table.Columns.SingleOrDefault(c => c.Id == node.Column) ?? throw new InvalidOperationException("Choose a valid filter column.");
            var op = node.Operator ?? "";
            if (!Operators(column).Contains(op)) throw new InvalidOperationException($"Operator '{op}' is not supported for {column.Name}.");
            var quoted = column.Type is "text" or "ntext" ? $"CONVERT(nvarchar(max), {SqlNames.Quote(column.Name)})" : SqlNames.Quote(column.Name);
            if (op == "null") return $"{quoted} IS NULL";
            if (op == "notnull") return $"{quoted} IS NOT NULL";
            if (op == "empty") return $"DATALENGTH({quoted}) = 0";
            if (op == "notempty") return $"DATALENGTH({quoted}) > 0";
            if (node.Value is null) throw new InvalidOperationException($"Enter a value for {column.Name}; use the NULL operator to match NULL.");
            var parameterName = "@filter" + parameters.Count;
            var parameter = column.Textual
                ? new SqlParameter(parameterName, SqlDbType.NVarChar, -1) { Value = node.Value }
                : SqlValues.Parameter(parameterName, column, node.Value);
            parameters.Add(parameter);
            return op switch
            {
                "contains" => $"CHARINDEX({parameterName}, {quoted}) > 0",
                "notcontains" => $"CHARINDEX({parameterName}, {quoted}) = 0",
                "starts" => $"LEFT({quoted}, LEN({parameterName} + N'#') - 1) = {parameterName}",
                "ends" => $"RIGHT({quoted}, LEN({parameterName} + N'#') - 1) = {parameterName}",
                _ => $"{quoted} {op switch { "eq" => "=", "ne" => "<>", "lt" => "<", "le" => "<=", "gt" => ">", "ge" => ">=", _ => throw new InvalidOperationException("Unknown filter operator.") }} {parameterName}"
            };
        }
        var predicate = Compile(root, 0);
        var order = new List<string>();
        if (sort.HasValue)
        {
            var column = table.Columns.SingleOrDefault(c => c.Id == sort) ?? throw new InvalidOperationException("Unknown sort column.");
            if (!column.Supported && !column.IsVersion || column.Type is "text" or "ntext")
                throw new InvalidOperationException("This column cannot be sorted directly. Use SQL with an explicit conversion.");
            order.Add(SqlNames.Quote(column.Name) + (descending ? " DESC" : " ASC"));
        }
        order.AddRange(table.Keys.Where(c => c.Id != sort).Select(c => SqlNames.Quote(c.Name)));
        if (order.Count == 0) order.Add("(SELECT NULL)");
        return new(predicate.Length == 0 ? "" : "\nWHERE " + predicate, string.Join(", ", order), parameters);
    }

    public static List<string> Operators(Column column)
    {
        var result = new List<string> { "null", "notnull" };
        if (!column.Supported) return result;
        result.InsertRange(0, ["eq", "ne"]);
        if (column.Type is not "bit" && column.Type is not "binary" and not "varbinary") result.AddRange(["lt", "le", "gt", "ge"]);
        if (column.Textual) result.AddRange(["contains", "notcontains", "starts", "ends", "empty", "notempty"]);
        return result;
    }
}

public static class SqlScript
{
    public static string Declare(SqlParameter p)
    {
        var type = p.SqlDbType switch
        {
            SqlDbType.Decimal => $"decimal({p.Precision},{p.Scale})",
            SqlDbType.NVarChar => "nvarchar(max)",
            SqlDbType.NChar => $"nchar({p.Size})",
            SqlDbType.VarChar => p.Size == -1 ? "varchar(max)" : $"varchar({p.Size})",
            SqlDbType.Char => $"char({p.Size})",
            SqlDbType.VarBinary => p.Size == -1 ? "varbinary(max)" : $"varbinary({p.Size})",
            SqlDbType.Binary => $"binary({p.Size})",
            SqlDbType.DateTime2 => $"datetime2({p.Scale})",
            SqlDbType.DateTimeOffset => $"datetimeoffset({p.Scale})",
            SqlDbType.Time => $"time({p.Scale})",
            _ => p.SqlDbType.ToString().ToLowerInvariant()
        };
        var value = p.Value switch
        {
            null or DBNull => "NULL",
            byte[] bytes => "0x" + Convert.ToHexString(bytes),
            bool boolean => boolean ? "1" : "0",
            string text => Quote(text),
            DateTime date => Quote(date.ToString("O", CultureInfo.InvariantCulture)),
            DateTimeOffset date => Quote(date.ToString("O", CultureInfo.InvariantCulture)),
            TimeSpan time => Quote(time.ToString("c", CultureInfo.InvariantCulture)),
            Guid guid => Quote(guid.ToString()),
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => Convert.ToString(p.Value, CultureInfo.InvariantCulture)
        };
        return $"DECLARE {p.ParameterName} {type} = {value};";
    }
    private static string Quote(string value) => "N'" + value.Replace("'", "''") + "'";
}
