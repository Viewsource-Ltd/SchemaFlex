using SchemaFlex.Core.Database;

namespace SchemaFlex.Tests.Unit;

public class SchemaDefaultsTests
{
    [Fact]
    public void Resolve_returns_public_for_postgres() =>
        Assert.Equal("public", SchemaDefaults.Resolve(DatabaseProvider.PostgreSql, "Host=localhost;Database=app"));

    [Fact]
    public void Resolve_returns_dbo_for_sqlserver() =>
        Assert.Equal("dbo", SchemaDefaults.Resolve(DatabaseProvider.SqlServer, "Data Source=localhost;Initial Catalog=app"));

    [Fact]
    public void Resolve_returns_database_name_for_mysql() =>
        Assert.Equal("app", SchemaDefaults.Resolve(DatabaseProvider.MySql, "Server=localhost;Database=app;Uid=root;Pwd=secret"));

    [Fact]
    public void Resolve_throws_for_mysql_without_a_database_name() =>
        Assert.Throws<ArgumentException>(() => SchemaDefaults.Resolve(DatabaseProvider.MySql, "Server=localhost;Uid=root;Pwd=secret"));

    [Fact]
    public void Resolve_returns_main_for_sqlite() =>
        Assert.Equal("main", SchemaDefaults.Resolve(DatabaseProvider.Sqlite, "Data Source=app.db"));
}
