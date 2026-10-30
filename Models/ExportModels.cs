namespace AdminerDOTNET.Models;

public record ExportSet(int Number, long Rows);
public sealed class ExportResult
{
    public string? Token { get; set; }
    public string? Error { get; set; }
    public List<ExportSet> Sets { get; } = [];
    public List<string> Messages { get; } = [];
    public int Affected { get; set; } = -1;
    public double Milliseconds { get; set; }
}
