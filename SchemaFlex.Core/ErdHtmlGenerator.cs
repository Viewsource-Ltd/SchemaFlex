using System.Reflection;
using System.Text.Json;
using SchemaFlex.Core.Models;

namespace SchemaFlex.Core;

public static class ErdHtmlGenerator
{
    private const string EmbeddedTemplateResourceName = "SchemaFlex.Core.templates.erd.html";

    public static string Generate(SchemaData schemaData) => Generate(schemaData, ReadEmbeddedTemplate());

    public static string Generate(SchemaData schemaData, string htmlTemplate) =>
        htmlTemplate.Replace("__SCHEMA_JSON__", ToEmbeddableJson(schemaData));

    // JSON is embedded inside a <script> block as a JS string literal; escaping "</"
    // prevents a stray "</script>" inside string data from closing the tag early.
    internal static string ToEmbeddableJson(SchemaData schemaData) =>
        JsonSerializer.Serialize(schemaData, SchemaJson.Options).Replace("</", "<\\/");

    public static string ReadEmbeddedTemplate() => ReadEmbeddedTemplate("erd.html");

    internal static string ReadEmbeddedTemplate(string fileName)
    {
        var resourceName = $"SchemaFlex.Core.templates.{fileName}";
        var assembly = Assembly.GetExecutingAssembly();

        using var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream == null)
        {
            throw new InvalidOperationException($"Could not load embedded template resource '{resourceName}'.");
        }

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
