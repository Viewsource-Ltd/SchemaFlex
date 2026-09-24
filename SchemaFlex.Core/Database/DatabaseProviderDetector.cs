using System.Data.Common;
using Microsoft.Data.SqlClient;
using MySqlConnector;
using Npgsql;

namespace SchemaFlex.Core.Database;

public static class DatabaseProviderDetector
{
    // Every key below is scored only if it is exclusive to that one provider. "Server",
    // "Host", "Data Source", "Uid" and "Pwd" all look like they belong to a single driver
    // but are NOT: Npgsql accepts Server/UID/PWD as aliases of Host/Username/Password,
    // Microsoft.Data.SqlClient accepts Uid/Pwd as aliases of User ID/Password, and
    // MySqlConnector accepts Data Source/DataSource/Host as aliases of Server. Scoring any
    // of those would let one provider "steal" points from a connection string that is
    // perfectly valid for a different one - which used to happen for real (see the
    // regression tests below). "Host" stays as a Postgres signal despite the alias overlap
    // because ties are resolved in Postgres's favour and nobody writes "Host=" meaning
    // MySQL in practice; every other shared key has been removed rather than guessed at.
    private static readonly string[] PostgresKeys =
        ["host", "server compatibility mode", "include error detail", "search path", "target session attributes", "no reset on close"];

    private static readonly string[] SqlServerKeys =
        ["initial catalog", "integrated security", "trustservercertificate", "encrypt", "multipleactiveresultsets", "workstation id", "multi subnet failover"];

    // "data source" is deliberately excluded here - both Microsoft.Data.Sqlite and
    // MySqlConnector accept the same key (Sqlite for its file path, MySqlConnector as a
    // synonym for "Server"), so it is disambiguated separately via LooksLikeSqlite below
    // rather than contributing to any provider's score.
    private static readonly string[] SqlServerDisambiguatingKeys = ["initial catalog", "integrated security", "trustservercertificate", "encrypt", "multipleactiveresultsets"];

    private static readonly string[] MySqlKeys =
        ["allowpublickeyretrieval", "sslmode", "server rsa public key file", "guidformat", "allowuservariables", "characterset", "useaffectedrows"];

    private static readonly string[] SqliteFileExtensions = [".db", ".sqlite", ".sqlite3", ".db3"];

    // A keyword match or a standard-port match is real evidence; this bonus just has to
    // outweigh every keyword score above put together so a port match always outranks a
    // partial keyword match rather than merely tying with it.
    private const int PortBonus = 100;

    public static DatabaseProvider Detect(string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new ArgumentException("A connection string must be provided.", nameof(connectionString));
        }

        var pairs = ParsePairs(connectionString);
        var keys = pairs.Keys;

        if (LooksLikeSqlite(pairs))
        {
            return DatabaseProvider.Sqlite;
        }

        var portProvider = PortProvider(pairs);
        var ranked = new (DatabaseProvider Provider, int Score)[]
        {
            (DatabaseProvider.PostgreSql, PostgresKeys.Count(keys.Contains) + (portProvider == DatabaseProvider.PostgreSql ? PortBonus : 0)),
            (DatabaseProvider.SqlServer, SqlServerKeys.Count(keys.Contains) + (portProvider == DatabaseProvider.SqlServer ? PortBonus : 0)),
            (DatabaseProvider.MySql, MySqlKeys.Count(keys.Contains) + (portProvider == DatabaseProvider.MySql ? PortBonus : 0))
        }.OrderByDescending(r => r.Score).ToArray();

        var topScore = ranked[0].Score;
        var topScorers = ranked.Where(r => r.Score == topScore).Select(r => r.Provider).ToArray();

        if (topScorers.Length == 1 && topScore > 0)
        {
            // A keyword or the port already points at one provider ahead of the others.
            // Confirm it against that provider's own connection string parser - which
            // knows its full, authoritative keyword set, not just the handful scored
            // above - and only fall through to the next-best candidate if it actually
            // rejects a keyword outright.
            foreach (var (provider, _) in ranked)
            {
                if (Accepts(provider, connectionString))
                {
                    return provider;
                }
            }

            throw NotRecognized();
        }

        // No single provider is clearly favoured - either nothing scored at all (a bare
        // "Server=...;Uid=...;Pwd=..." style string, valid for every provider) or two
        // providers matched the same number of exclusive keywords. Keyword scoring alone
        // can't settle this, so ask each tied candidate's own parser whether it actually
        // accepts every keyword in the string. If exactly one does, that's a real answer;
        // if none or more than one do, there genuinely isn't enough information to choose
        // safely, and guessing would be wrong as often as it was right.
        var accepting = topScorers.Where(p => Accepts(p, connectionString)).ToArray();
        if (accepting.Length == 1)
        {
            return accepting[0];
        }

        if (accepting.Length == 0)
        {
            throw NotRecognized();
        }

        throw new ArgumentException(
            $"Connection string is valid for more than one database provider ({string.Join(", ", accepting)}) " +
            "and could not be disambiguated. Add a distinguishing option (e.g. Port) or construct the " +
            "schema reader for the intended provider directly.",
            nameof(connectionString));
    }

    private static bool Accepts(DatabaseProvider provider, string connectionString)
    {
        try
        {
            switch (provider)
            {
                case DatabaseProvider.PostgreSql: _ = new NpgsqlConnectionStringBuilder(connectionString); return true;
                case DatabaseProvider.SqlServer: _ = new SqlConnectionStringBuilder(connectionString); return true;
                case DatabaseProvider.MySql: _ = new MySqlConnectionStringBuilder(connectionString); return true;
                default: return false;
            }
        }
        catch (ArgumentException)
        {
            // The provider's own builder rejected a keyword it doesn't recognize - this
            // connection string cannot actually be for this provider, whatever it scored.
            return false;
        }
    }

    private static ArgumentException NotRecognized() => new(
        "Could not auto-detect the database provider from the connection string. " +
        "Recognized formats are Npgsql (e.g. 'Host=...'), SqlClient (e.g. 'Data Source=...;Initial Catalog=...'), " +
        "MySqlConnector (e.g. 'Server=...;Uid=...;Pwd=...') and Microsoft.Data.Sqlite (e.g. 'Data Source=app.db'). " +
        "If none of those keys are present, add the provider's standard port or construct the schema reader directly.",
        "connectionString");

    private static DatabaseProvider? PortProvider(Dictionary<string, string> pairs)
    {
        // No SQL Server case here: Microsoft.Data.SqlClient has no "Port" keyword at all -
        // its connection string encodes a non-default port inline in "Data Source"/"Server"
        // (e.g. "Server=host,1433"), never as a separate key. Any connection string that
        // literally contains "Port=..." therefore cannot be a real SqlClient connection
        // string regardless of the number, so scoring 1433 here would only ever point at a
        // candidate that Accepts() below is guaranteed to reject.
        if (pairs.TryGetValue("port", out var port))
        {
            switch (port)
            {
                case "5432": return DatabaseProvider.PostgreSql;
                case "3306": return DatabaseProvider.MySql;
            }
        }

        return null;
    }

    private static bool LooksLikeSqlite(Dictionary<string, string> pairs)
    {
        if (!pairs.TryGetValue("data source", out var dataSource) || string.IsNullOrWhiteSpace(dataSource))
        {
            return false;
        }

        // A real SQL Server connection string always carries at least one of these
        // alongside "Data Source" - their absence is what makes a bare "Data Source=..."
        // ambiguous enough to check for SQLite-shaped values instead.
        if (pairs.Keys.Any(SqlServerDisambiguatingKeys.Contains))
        {
            return false;
        }

        if (pairs.ContainsKey("mode") || pairs.ContainsKey("cache"))
        {
            return true;
        }

        if (string.Equals(dataSource, ":memory:", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return SqliteFileExtensions.Any(ext => dataSource.EndsWith(ext, StringComparison.OrdinalIgnoreCase));
    }

    private static Dictionary<string, string> ParsePairs(string connectionString)
    {
        var builder = new DbConnectionStringBuilder { ConnectionString = connectionString };
        return builder.Keys
            .Cast<string>()
            .ToDictionary(k => k.ToLowerInvariant(), k => builder[k]?.ToString() ?? string.Empty);
    }
}
