namespace SchemaFlex.Core.Models;

public record IndexInfo(string Name, List<string> Columns, bool IsUnique, string Method);
