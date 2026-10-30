using System.Text.Json;

namespace AdminerDOTNET.Models;

public sealed class FilterNode
{
    public string? Logic { get; set; }
    public List<FilterNode>? Children { get; set; }
    public int? Column { get; set; }
    public string? Operator { get; set; }
    public string? Value { get; set; }
}

public static class FilterState
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { MaxDepth = 24, DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };
    public static string Serialize(FilterNode node) => JsonSerializer.Serialize(node, Json);
    public static FilterNode Parse(string? json)
    {
        if (json is null) return new() { Logic = "and", Children = [] };
        if (json.Length > 6000) throw new InvalidOperationException("The filter definition is too large. Use fewer conditions or shorter values (maximum 6,000 characters).");
        try { return JsonSerializer.Deserialize<FilterNode>(json, Json) ?? throw new JsonException(); }
        catch (JsonException) { throw new InvalidOperationException("The filter definition is invalid. Clear the filters and try again."); }
    }
}
