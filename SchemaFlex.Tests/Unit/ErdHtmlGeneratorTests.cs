using SchemaFlex.Core;
using SchemaFlex.Core.Database;
using SchemaFlex.Core.Models;

namespace SchemaFlex.Tests.Unit;

public class ErdHtmlGeneratorTests
{
    private const string Template = "<html><script>const data = \"__SCHEMA_JSON__\";</script></html>";

    [Fact]
    public void Generate_embeds_camelCase_json_into_the_template()
    {
        var schemaData = new SchemaData(
            "public",
            new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero),
            new List<Table>
            {
                new("users", new List<Column>(), new List<ForeignKey>(), new List<CheckConstraint>(), new List<IndexInfo>(), new List<TriggerInfo>(), null),
            },
            new List<EnumType>(),
            DatabaseProvider.PostgreSql);

        var html = ErdHtmlGenerator.Generate(schemaData, Template);

        Assert.Contains("\"generatedAt\"", html);
        Assert.Contains("\"tables\"", html);
        Assert.DoesNotContain("__SCHEMA_JSON__", html);
    }

    [Fact]
    public void Generate_escapes_closing_script_tags_inside_embedded_data()
    {
        var schemaData = new SchemaData(
            "public",
            DateTimeOffset.UtcNow,
            new List<Table>
            {
                new(
                    "users",
                    new List<Column>
                    {
                        new("bio", "text", true, false, false, null, false, null, "</script><script>alert(1)</script>"),
                    },
                    new List<ForeignKey>(),
                    new List<CheckConstraint>(),
                    new List<IndexInfo>(),
                    new List<TriggerInfo>(),
                    null),
            },
            new List<EnumType>(),
            DatabaseProvider.PostgreSql);

        var html = ErdHtmlGenerator.Generate(schemaData, Template);

        // Only the template's own closing tag should remain - the malicious payload
        // embedded in the comment must not produce an extra one.
        var occurrences = html.Split("</script>", StringSplitOptions.None).Length - 1;
        Assert.Equal(1, occurrences);
    }
}
