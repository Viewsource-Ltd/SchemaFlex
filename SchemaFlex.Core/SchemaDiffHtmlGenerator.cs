using SchemaFlex.Core.Models;

namespace SchemaFlex.Core;

public static class SchemaDiffHtmlGenerator
{
    public static string Generate(SchemaData before, SchemaData after) =>
        Generate(before, after, ErdHtmlGenerator.ReadEmbeddedTemplate("erd-diff.html"));

    public static string Generate(SchemaData before, SchemaData after, string htmlTemplate) => htmlTemplate
        .Replace("__SCHEMA_JSON_A__", ErdHtmlGenerator.ToEmbeddableJson(before))
        .Replace("__SCHEMA_JSON_B__", ErdHtmlGenerator.ToEmbeddableJson(after));
}
