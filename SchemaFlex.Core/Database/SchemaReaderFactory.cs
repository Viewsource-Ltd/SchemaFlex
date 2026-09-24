using SchemaFlex.Core.Database.MySql;
using SchemaFlex.Core.Database.Postgres;
using SchemaFlex.Core.Database.Sqlite;
using SchemaFlex.Core.Database.SqlServer;

namespace SchemaFlex.Core.Database;

public static class SchemaReaderFactory
{
    public static ISchemaReader Create(DatabaseProvider provider) => provider switch
    {
        DatabaseProvider.PostgreSql => new PostgresSchemaReader(),
        DatabaseProvider.SqlServer => new SqlServerSchemaReader(),
        DatabaseProvider.MySql => new MySqlSchemaReader(),
        DatabaseProvider.Sqlite => new SqliteSchemaReader(),
        _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, "Unsupported database provider."),
    };
}
