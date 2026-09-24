using Microsoft.Data.SqlClient;
using MySqlConnector;
using Npgsql;
using SchemaFlex.Core.Database;
using System.Text.RegularExpressions;

namespace SchemaFlex.Core.Ssh;

/// <summary>
/// Reads and rewrites the host/port a connection string points at, per provider - used to
/// swap the real database endpoint for an SSH-tunneled local port without touching anything
/// else in the connection string (credentials, database name, TLS options, ...).
/// </summary>
public static class ConnectionEndpoint
{
    public static (string Host, int Port) Resolve(DatabaseProvider provider, string connectionString)
    {
        switch (provider)
        {
            case DatabaseProvider.PostgreSql:
                var npgsql = new NpgsqlConnectionStringBuilder(connectionString);
                return (npgsql.Host ?? "localhost", npgsql.Port is > 0 ? npgsql.Port : 5432);

            case DatabaseProvider.MySql:
                var mysql = new MySqlConnectionStringBuilder(connectionString);
                return (string.IsNullOrEmpty(mysql.Server) ? "localhost" : mysql.Server, (int)mysql.Port);

            case DatabaseProvider.SqlServer:
                return ParseSqlServerDataSource(new SqlConnectionStringBuilder(connectionString).DataSource);

            case DatabaseProvider.Sqlite:
                throw new ArgumentException("SQLite is file-based and has no host/port to tunnel to.", nameof(provider));

            default:
                throw new ArgumentOutOfRangeException(nameof(provider), provider, "Unknown database provider.");
        }
    }

    public static string WithHostAndPort(DatabaseProvider provider, string connectionString, string host, int port)
    {
        switch (provider)
        {
            case DatabaseProvider.PostgreSql:
                var npgsql = new NpgsqlConnectionStringBuilder(connectionString) { Host = host, Port = port };
                return npgsql.ConnectionString;

            case DatabaseProvider.MySql:
                var mysql = new MySqlConnectionStringBuilder(connectionString) { Server = host, Port = (uint)port };
                return mysql.ConnectionString;

            case DatabaseProvider.SqlServer:
                var sqlServer = new SqlConnectionStringBuilder(connectionString) { DataSource = $"{host},{port}" };
                return sqlServer.ConnectionString;

            case DatabaseProvider.Sqlite:
                throw new ArgumentException("SQLite is file-based and has no host/port to tunnel to.", nameof(provider));

            default:
                throw new ArgumentOutOfRangeException(nameof(provider), provider, "Unknown database provider.");
        }
    }

    // SqlConnectionStringBuilder.DataSource can carry a protocol prefix, a named instance,
    // and/or a port all bundled into one string (e.g. "tcp:myserver,1433", "myserver\SQLEXPRESS").
    // Only the plain "host" and "host,port" forms have an unambiguous single endpoint to
    // forward - a named instance additionally needs the SQL Browser UDP service on the
    // target network to resolve its port, which a single TCP tunnel can't provide.
    private static readonly Regex SimpleDataSource = new(@"^(?<host>[^,\\]+)(,(?<port>\d+))?$", RegexOptions.Compiled);

    private static (string Host, int Port) ParseSqlServerDataSource(string? dataSource)
    {
        if (string.IsNullOrWhiteSpace(dataSource))
        {
            throw new ArgumentException("The connection string has no Data Source to tunnel to.", nameof(dataSource));
        }

        var match = SimpleDataSource.Match(dataSource.Trim());
        if (!match.Success)
        {
            throw new ArgumentException(
                $"Data Source '{dataSource}' is not a plain 'host' or 'host,port' address, so it can't be tunneled over " +
                "SSH automatically. Named instances and protocol prefixes need a distinguishing option; connect to the " +
                "instance's port directly instead (e.g. 'host,1433').",
                nameof(dataSource));
        }

        var host = match.Groups["host"].Value;
        var port = match.Groups["port"].Success ? int.Parse(match.Groups["port"].Value) : 1433;
        return (host, port);
    }
}
