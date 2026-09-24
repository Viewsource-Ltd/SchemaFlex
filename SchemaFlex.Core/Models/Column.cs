namespace SchemaFlex.Core.Models;

public record Column(
    string Name,
    string DataType,
    bool IsNullable,
    bool IsPrimaryKey,
    bool IsUnique,
    string? Default,
    bool IsIdentity,
    string? GeneratedExpression,
    string? Comment);
