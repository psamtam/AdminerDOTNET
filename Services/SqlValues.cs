using System.Data;
using System.Data.SqlTypes;
using System.Globalization;
using AdminerDOTNET.Models;
using Microsoft.Data.SqlClient;

namespace AdminerDOTNET.Services;

public static class SqlValues
{
    public static string? Read(SqlDataReader reader, int index)
    {
        if (reader.IsDBNull(index)) return null;
        if (reader.GetDataTypeName(index) is "decimal" or "numeric") return reader.GetSqlDecimal(index).ToString();
        return reader.GetValue(index) switch
        {
            byte[] bytes => "0x" + Convert.ToHexString(bytes),
            DateTime date => date.ToString("O", CultureInfo.InvariantCulture),
            DateTimeOffset date => date.ToString("O", CultureInfo.InvariantCulture),
            TimeSpan time => time.ToString("c", CultureInfo.InvariantCulture),
            bool boolean => boolean ? "1" : "0",
            IFormattable value => value.ToString(null, CultureInfo.InvariantCulture),
            object value => value.ToString()
        };
    }

    public static SqlParameter Parameter(string name, Column column, string? text)
    {
        var type = column.Type switch
        {
            "numeric" => SqlDbType.Decimal,
            "timestamp" or "rowversion" => SqlDbType.Timestamp,
            _ => Enum.Parse<SqlDbType>(column.Type, true)
        };
        var parameter = new SqlParameter(name, type);
        if (column.Type is "nchar" or "nvarchar") parameter.Size = column.Length == -1 ? -1 : column.Length / 2;
        if (column.Type is "char" or "varchar" or "binary" or "varbinary") parameter.Size = column.Length;
        if (column.Type is "decimal" or "numeric") { parameter.Precision = column.Precision; parameter.Scale = column.Scale; }
        if (column.Type is "datetime2" or "datetimeoffset" or "time") parameter.Scale = column.Scale;
        if (text is null) { parameter.Value = DBNull.Value; return parameter; }
        object value = column.Type switch
        {
            "bigint" => long.Parse(text, CultureInfo.InvariantCulture),
            "int" => int.Parse(text, CultureInfo.InvariantCulture),
            "smallint" => short.Parse(text, CultureInfo.InvariantCulture),
            "tinyint" => byte.Parse(text, CultureInfo.InvariantCulture),
            "bit" => text.Trim().ToLowerInvariant() switch { "1" or "true" => true, "0" or "false" => false, _ => throw new FormatException("Use 1 or 0 for bit values.") },
            "decimal" or "numeric" => SqlDecimal.Parse(text),
            "money" or "smallmoney" => decimal.Parse(text, CultureInfo.InvariantCulture),
            "float" => double.Parse(text, CultureInfo.InvariantCulture),
            "real" => float.Parse(text, CultureInfo.InvariantCulture),
            "uniqueidentifier" => Guid.Parse(text),
            "date" or "datetime" or "datetime2" or "smalldatetime" => DateTime.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            "datetimeoffset" => DateTimeOffset.Parse(text, CultureInfo.InvariantCulture),
            "time" => TimeSpan.Parse(text, CultureInfo.InvariantCulture),
            "binary" or "varbinary" or "timestamp" or "rowversion" => Convert.FromHexString(text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? text[2..] : text),
            _ => text
        };
        if (value is string str && column.Length != -1 && column.Type is "char" or "varchar" or "nchar" or "nvarchar")
        {
            var max = column.Type.StartsWith('n') ? column.Length / 2 : column.Length;
            if (str.Length > max) throw new FormatException($"{column.Name} exceeds its maximum length of {max}.");
        }
        parameter.Value = value;
        return parameter;
    }
}
