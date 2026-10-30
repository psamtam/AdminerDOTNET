namespace AdminerDOTNET.Models;

public record CompletionColumn(string Name, string Type);
public record CompletionObject(int Id, string Schema, string Name, bool IsView, List<CompletionColumn> Columns);
public record CompletionMetadata(string Database, List<CompletionObject> Objects, string DefaultSchema = "dbo");
