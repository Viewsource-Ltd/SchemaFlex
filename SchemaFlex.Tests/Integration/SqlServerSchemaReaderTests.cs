using Microsoft.Data.SqlClient;
using SchemaFlex.Core.Database.SqlServer;
using Testcontainers.MsSql;
using Xunit;

namespace SchemaFlex.Tests.Integration;

[Trait("Category", "Integration")]
public class SqlServerSchemaReaderTests : IAsyncLifetime
{
    private readonly MsSqlContainer _container = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest").Build();

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync();

        await using var conn = new SqlConnection(_container.GetConnectionString());
        await conn.OpenAsync();

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE authors (
                id INT IDENTITY PRIMARY KEY,
                name NVARCHAR(200) NOT NULL UNIQUE,
                email NVARCHAR(200)
            );

            EXEC sp_addextendedproperty @name = N'MS_Description', @value = N'People who write posts',
                @level0type = N'Schema', @level0name = 'dbo', @level1type = N'Table', @level1name = 'authors';
            EXEC sp_addextendedproperty @name = N'MS_Description', @value = N'Contact address',
                @level0type = N'Schema', @level0name = 'dbo', @level1type = N'Table', @level1name = 'authors',
                @level2type = N'Column', @level2name = 'email';

            CREATE TABLE posts (
                id INT IDENTITY PRIMARY KEY,
                author_id INT NOT NULL REFERENCES authors(id) ON DELETE CASCADE,
                title NVARCHAR(200) NOT NULL,
                view_count INT NOT NULL DEFAULT 0 CHECK (view_count >= 0)
            );
            CREATE INDEX idx_posts_title ON posts(title);

            EXEC('CREATE TRIGGER posts_touch_updated_at ON posts AFTER UPDATE AS BEGIN SET NOCOUNT ON; END');

            EXEC('CREATE VIEW post_summary AS
                SELECT p.id, p.title, a.name AS author_name
                FROM posts p JOIN authors a ON a.id = p.author_id');

            EXEC('CREATE VIEW post_summary_cte AS
                WITH ranked AS (SELECT * FROM posts)
                SELECT r.id, a.name AS author_name
                FROM ranked r JOIN authors a ON a.id = r.author_id');
            """;
        await cmd.ExecuteNonQueryAsync();
    }

    public async ValueTask DisposeAsync() => await _container.DisposeAsync();

    [Fact]
    public async Task ReadSchemaAsync_reads_tables_columns_and_constraints()
    {
        var reader = new SqlServerSchemaReader();

        var schemaData = await reader.ReadSchemaAsync(_container.GetConnectionString(), "dbo", CancellationToken.None);

        Assert.Equal(
            new[] { "authors", "post_summary", "post_summary_cte", "posts" }.OrderBy(n => n, StringComparer.OrdinalIgnoreCase),
            schemaData.Tables.Select(t => t.Name));

        var authors = schemaData.Tables.Single(t => t.Name == "authors");
        Assert.Equal("People who write posts", authors.Comment);
        var idColumn = authors.Columns.Single(c => c.Name == "id");
        Assert.True(idColumn.IsPrimaryKey);
        Assert.True(idColumn.IsIdentity);
        var nameColumn = authors.Columns.Single(c => c.Name == "name");
        Assert.True(nameColumn.IsUnique);
        var emailColumn = authors.Columns.Single(c => c.Name == "email");
        Assert.Equal("Contact address", emailColumn.Comment);

        var posts = schemaData.Tables.Single(t => t.Name == "posts");
        var fk = Assert.Single(posts.ForeignKeys);
        Assert.Equal("author_id", fk.Column);
        Assert.Equal("authors", fk.RefTable);
        Assert.Equal("id", fk.RefColumn);
        Assert.Equal("CASCADE", fk.OnDelete);

        var check = Assert.Single(posts.CheckConstraints);
        Assert.Contains("view_count", check.Definition);

        var index = Assert.Single(posts.Indexes);
        Assert.Equal("idx_posts_title", index.Name);
        Assert.Contains("title", index.Columns);

        var trigger = Assert.Single(posts.Triggers);
        Assert.Equal("posts_touch_updated_at", trigger.Name);
        Assert.Equal("AFTER", trigger.Timing);
        Assert.True(trigger.IsEnabled);

        Assert.Empty(schemaData.Enums);

        var postSummary = schemaData.Tables.Single(t => t.Name == "post_summary");
        Assert.True(postSummary.IsView);
        Assert.Equal(
            new[] { "authors", "posts" },
            postSummary.ViewDependencies.OrderBy(n => n, StringComparer.OrdinalIgnoreCase));
        Assert.Contains("CREATE VIEW", postSummary.Definition, StringComparison.OrdinalIgnoreCase);

        // The "ranked" CTE alias must not leak into the dependency list - only the
        // real tables the view actually reads from should show up.
        var postSummaryCte = schemaData.Tables.Single(t => t.Name == "post_summary_cte");
        Assert.True(postSummaryCte.IsView);
        Assert.Equal(
            new[] { "authors", "posts" },
            postSummaryCte.ViewDependencies.OrderBy(n => n, StringComparer.OrdinalIgnoreCase));
    }
}
