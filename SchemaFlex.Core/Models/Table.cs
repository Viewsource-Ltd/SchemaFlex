namespace SchemaFlex.Core.Models;

public record Table(
    string Name,
    List<Column> Columns,
    List<ForeignKey> ForeignKeys,
    List<CheckConstraint> CheckConstraints,
    List<IndexInfo> Indexes,
    List<TriggerInfo> Triggers,
    string? Comment,
    bool IsView = false);
