using System.Text.Json;
using System.Text.Json.Serialization;

namespace SchemaFlex.Core;

/// <summary>
/// The single JSON shape shared by everything that embeds or reads back a
/// <see cref="Models.SchemaData"/> - keeping the options in one place is what keeps a
/// generated file and the reader that later extracts it from it in sync.
/// </summary>
internal static class SchemaJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };
}
