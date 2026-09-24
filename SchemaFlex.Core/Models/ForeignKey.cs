namespace SchemaFlex.Core.Models;

public record ForeignKey(
    string ConstraintName,
    string Column,
    string RefTable,
    string RefColumn,
    string? OnDelete,
    string? OnUpdate);
