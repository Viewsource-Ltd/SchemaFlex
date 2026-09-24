using SchemaFlex.Core.Database;

namespace SchemaFlex.Tests.Unit;

public class DatabaseProviderDetectorTests
{
    [Theory]
    [InlineData("Host=localhost;Username=postgres;Password=secret;Database=app", DatabaseProvider.PostgreSql)]
    [InlineData("Host=localhost;Port=5432;Username=postgres;Password=secret;Database=app", DatabaseProvider.PostgreSql)]
    [InlineData("Data Source=localhost;Initial Catalog=app;Integrated Security=True;TrustServerCertificate=True", DatabaseProvider.SqlServer)]
    [InlineData("Server=localhost;Database=app;User Id=sa;Password=secret;Encrypt=True", DatabaseProvider.SqlServer)]
    [InlineData("Server=localhost;Database=app;Uid=root;Pwd=secret;AllowPublicKeyRetrieval=True", DatabaseProvider.MySql)]
    [InlineData("Server=localhost;Port=3306;Database=app;Uid=root;Pwd=secret", DatabaseProvider.MySql)]
    [InlineData("Data Source=app.db", DatabaseProvider.Sqlite)]
    [InlineData("Data Source=/var/data/app.sqlite3", DatabaseProvider.Sqlite)]
    [InlineData("Data Source=:memory:", DatabaseProvider.Sqlite)]
    [InlineData("Data Source=file::memory:;Mode=Memory;Cache=Shared", DatabaseProvider.Sqlite)]
    public void Detect_returns_expected_provider_for_recognized_connection_strings(string connectionString, DatabaseProvider expected)
    {
        var actual = DatabaseProviderDetector.Detect(connectionString);

        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("Server=localhost;Port=5432;Database=app", DatabaseProvider.PostgreSql)]
    [InlineData("Server=localhost;Port=3306;Database=app", DatabaseProvider.MySql)]
    public void Detect_falls_back_to_port_when_keys_are_ambiguous(string connectionString, DatabaseProvider expected)
    {
        var actual = DatabaseProviderDetector.Detect(connectionString);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Detect_does_not_fall_back_to_port_1433_for_sql_server()
    {
        // Unlike Postgres and MySQL, Microsoft.Data.SqlClient has no "Port" keyword at all -
        // a real SQL Server connection string encodes a non-default port inline in
        // "Data Source" (e.g. "Server=host,1433"), never as a separate key. So a literal
        // "Port=1433" can never actually be a SQL Server connection string, and here it's
        // valid for both Postgres and MySQL (both do have a real Port keyword) with nothing
        // else to tell them apart - genuinely ambiguous, so this throws rather than guessing
        // "SqlServer" for a string that SqlClient itself would immediately reject anyway.
        Assert.Throws<ArgumentException>(() => DatabaseProviderDetector.Detect("Server=localhost;Port=1433;Database=app"));
    }

    // Each of these uses a key that looks like it belongs to one provider but is actually a
    // documented alias in a different provider's connection string builder too:
    //   - Npgsql accepts "Server" (alias of Host) and "UID"/"PWD" (aliases of Username/Password)
    //   - Microsoft.Data.SqlClient accepts "Uid"/"Pwd" (aliases of User ID/Password)
    //   - MySqlConnector accepts "Data Source"/"DataSource" (aliases of Server)
    // Scoring those keys as if they were provider-exclusive used to misdetect all three of
    // these as the wrong provider.
    [Theory]
    [InlineData("Server=localhost;Port=5432;Database=app;Uid=postgres;Pwd=secret", DatabaseProvider.PostgreSql)]
    [InlineData("Server=localhost;Database=app;Uid=sa;Pwd=secret;Encrypt=True", DatabaseProvider.SqlServer)]
    [InlineData("Data Source=localhost;Port=3306;Database=app;User Id=root;Password=secret", DatabaseProvider.MySql)]
    public void Detect_is_not_fooled_by_keys_shared_across_providers(string connectionString, DatabaseProvider expected)
    {
        var actual = DatabaseProviderDetector.Detect(connectionString);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Detect_falls_through_to_the_next_candidate_when_the_top_scorer_rejects_the_string()
    {
        // "Initial Catalog" makes SqlServer the unique top scorer, but this string also
        // carries "Port" - which SqlConnectionStringBuilder has no keyword for at all - so
        // SqlServer's own parser rejects it outright. Npgsql also rejects it ("Initial
        // Catalog" isn't one of its keywords), leaving MySqlConnector - which tolerates
        // both keys - as the only candidate that actually accepts every keyword present.
        var actual = DatabaseProviderDetector.Detect("Server=localhost;Port=1433;Initial Catalog=app");

        Assert.Equal(DatabaseProvider.MySql, actual);
    }

    [Fact]
    public void Detect_throws_for_unrecognized_connection_string()
    {
        Assert.Throws<ArgumentException>(() => DatabaseProviderDetector.Detect("Server=localhost;Database=app"));
    }

    [Fact]
    public void Detect_throws_rather_than_guess_when_truly_ambiguous()
    {
        // "Data Source=...;Database=..." with no port and no other keys is complete, valid
        // syntax for both SqlClient (Data Source/Database) and MySqlConnector (Data Source
        // is a documented synonym for Server). There is genuinely no way to tell which one
        // was intended, so this must throw rather than silently pick one.
        Assert.Throws<ArgumentException>(() => DatabaseProviderDetector.Detect("Data Source=localhost;Database=app"));
    }

    [Fact]
    public void Detect_throws_for_null_or_empty_connection_string()
    {
        Assert.Throws<ArgumentException>(() => DatabaseProviderDetector.Detect(""));
    }
}
