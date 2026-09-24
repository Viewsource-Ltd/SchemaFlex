namespace SchemaFlex.Core.Models;

public record TriggerInfo(
    string Name,
    string Timing,
    string Level,
    List<string> Events,
    string Statement,
    bool IsEnabled);
