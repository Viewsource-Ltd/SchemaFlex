using Microsoft.Data.Sqlite;
using SchemaFlex.Core.Database.Sqlite;
using Xunit;

namespace SchemaFlex.Tests.Integration;

[Trait("Category", "Integration")]
public class SqliteSchemaReaderTests : IAsyncLifetime
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"schemaflex_{Guid.NewGuid():N}.db");

    private string ConnectionString => $"Data Source={_dbPath}";

    public async ValueTask InitializeAsync()
    {
        await using var conn = new SqliteConnection(ConnectionString);
        await conn.OpenAsync();

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE authors (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                name TEXT NOT NULL UNIQUE,
                email TEXT
            );

            CREATE TABLE posts (
                id INTEGER PRIMARY KEY,
                author_id INTEGER NOT NULL,
                title TEXT NOT NULL,
                view_count INTEGER NOT NULL DEFAULT 0 CHECK (view_count >= 0),
                mood TEXT,
                FOREIGN KEY (author_id) REFERENCES authors(id) ON DELETE CASCADE
            );
            CREATE INDEX idx_posts_title ON posts(title);

            CREATE TRIGGER posts_touch_updated_at AFTER UPDATE ON posts BEGIN SELECT 1; END;

            CREATE VIEW post_titles AS SELECT id, title FROM posts;

            CREATE VIEW post_summary AS
                SELECT p.id, p.title, a.name AS author_name
                FROM posts p JOIN authors a ON a.id = p.author_id;

            CREATE VIEW post_summary_cte AS
                WITH ranked AS (SELECT * FROM posts)
                SELECT r.id, a.name AS author_name
                FROM ranked r JOIN authors a ON a.id = r.author_id;
            """;
        await cmd.ExecuteNonQueryAsync();
    }

    public ValueTask DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task ReadSchemaAsync_reads_tables_columns_and_constraints()
    {
        var reader = new SqliteSchemaReader();

        var schemaData = await reader.ReadSchemaAsync(ConnectionString, "main", CancellationToken.None);

        Assert.Equal(
            new[] { "authors", "post_summary", "post_summary_cte", "post_titles", "posts" }.OrderBy(n => n, StringComparer.OrdinalIgnoreCase),
            schemaData.Tables.Select(t => t.Name));

        var authors = schemaData.Tables.Single(t => t.Name == "authors");
        Assert.False(authors.IsView);
        var idColumn = authors.Columns.Single(c => c.Name == "id");
        Assert.True(idColumn.IsPrimaryKey);
        Assert.True(idColumn.IsIdentity);
        var nameColumn = authors.Columns.Single(c => c.Name == "name");
        Assert.True(nameColumn.IsUnique);

        var postTitles = schemaData.Tables.Single(t => t.Name == "post_titles");
        Assert.True(postTitles.IsView);
        Assert.Equal(new[] { "id", "title" }, postTitles.Columns.Select(c => c.Name));
        Assert.Equal(new[] { "posts" }, postTitles.ViewDependencies);
        Assert.Contains("SELECT", postTitles.Definition, StringComparison.OrdinalIgnoreCase);

        var postSummary = schemaData.Tables.Single(t => t.Name == "post_summary");
        Assert.True(postSummary.IsView);
        Assert.Equal(
            new[] { "authors", "posts" },
            postSummary.ViewDependencies.OrderBy(n => n, StringComparer.OrdinalIgnoreCase));

        // The "ranked" CTE alias must not leak into the dependency list - only the
        // real tables it (and the outer query) actually scan should show up.
        var postSummaryCte = schemaData.Tables.Single(t => t.Name == "post_summary_cte");
        Assert.True(postSummaryCte.IsView);
        Assert.Equal(
            new[] { "authors", "posts" },
            postSummaryCte.ViewDependencies.OrderBy(n => n, StringComparer.OrdinalIgnoreCase));

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
    }
}
