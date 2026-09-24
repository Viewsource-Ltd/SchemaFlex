using SchemaFlex.Core;
using SchemaFlex.Core.Database;
using SchemaFlex.Core.Models;

namespace SchemaFlex.Tests.Unit;

public class SchemaDiffHtmlGeneratorTests
{
    private const string Template =
        "<html><body><script>\n" +
        "const schemaDataA = __SCHEMA_JSON_A__;\n" +
        "const schemaDataB = __SCHEMA_JSON_B__;\n" +
        "</script></body></html>";

    private static SchemaData MakeSchema(string tableName) => new(
        "public",
        new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero),
        new List<Table>
        {
            new(tableName, new List<Column>(), new List<ForeignKey>(), new List<CheckConstraint>(), new List<IndexInfo>(), new List<TriggerInfo>(), null),
        },
        new List<EnumType>(),
        DatabaseProvider.PostgreSql);

    [Fact]
    public void Generate_embeds_both_schemas_at_their_own_placeholders()
    {
        var before = MakeSchema("customers");
        var after = MakeSchema("clients");

        var html = SchemaDiffHtmlGenerator.Generate(before, after, Template);

        Assert.DoesNotContain("__SCHEMA_JSON_A__", html);
        Assert.DoesNotContain("__SCHEMA_JSON_B__", html);

        const string markerA = "const schemaDataA = ";
        const string markerB = "const schemaDataB = ";
        var aIndex = html.IndexOf(markerA, StringComparison.Ordinal);
        var bIndex = html.IndexOf(markerB, StringComparison.Ordinal);
        Assert.True(aIndex >= 0 && bIndex > aIndex);

        var aJson = html[(aIndex + markerA.Length)..bIndex].TrimEnd().TrimEnd(';');
        Assert.Contains("\"customers\"", aJson);
        Assert.DoesNotContain("\"clients\"", aJson);
    }

    [Fact]
    public void Generate_keeps_the_two_schemas_independent_even_when_identical_shape()
    {
        var before = MakeSchema("widgets");
        var after = MakeSchema("widgets_v2");

        var html = SchemaDiffHtmlGenerator.Generate(before, after, Template);

        Assert.Contains("\"widgets\"", html);
        Assert.Contains("\"widgets_v2\"", html);
    }
}
