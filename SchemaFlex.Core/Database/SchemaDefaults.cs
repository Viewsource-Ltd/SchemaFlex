using System.Data.Common;

namespace SchemaFlex.Core.Database;

public static class SchemaDefaults
{
    /// <summary>
    /// Postgres and SQL Server have a "schema" namespace distinct from the database/catalog;
    /// MySQL/MariaDB do not - "schema" there just means the database name from the connection
    /// string, so no separate default applies. SQLite has neither concept - "schema" there
    /// means the name of an attached database, and "main" (the primary database file) always
    /// exists.
    /// </summary>
    public static string Resolve(DatabaseProvider provider, string connectionString) => provider switch
    {
        DatabaseProvider.PostgreSql => "public",
        DatabaseProvider.SqlServer => "dbo",
        DatabaseProvider.MySql => ExtractDatabaseName(connectionString),
        DatabaseProvider.Sqlite => "main",
        _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, "Unsupported database provider."),
    };

    private static string ExtractDatabaseName(string connectionString)
    {
        var builder = new DbConnectionStringBuilder { ConnectionString = connectionString };
        foreach (var key in new[] { "database", "initial catalog" })
        {
            if (builder.TryGetValue(key, out var value) && value is string { Length: > 0 } name)
            {
                return name;
            }
        }

        throw new ArgumentException(
            "Could not determine the MySQL/MariaDB database name from the connection string; " +
            "include a 'Database=' key or pass --schema explicitly.",
            nameof(connectionString));
    }
}
