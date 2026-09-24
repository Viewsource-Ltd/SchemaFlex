using SchemaFlex.Core.Models;

namespace SchemaFlex.Core.Database;

public interface ISchemaReader
{
    Task<SchemaData> ReadSchemaAsync(string connectionString, string schema, CancellationToken token);
}
