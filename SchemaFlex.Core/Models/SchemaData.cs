using SchemaFlex.Core.Database;

namespace SchemaFlex.Core.Models;

public record SchemaData(string Schema, DateTimeOffset GeneratedAt, List<Table> Tables, List<EnumType> Enums, DatabaseProvider Provider);
