using MySqlConnector;
using SchemaFlex.Core.Database.MySql;
using Testcontainers.MySql;
using Xunit;

namespace SchemaFlex.Tests.Integration;

[Trait("Category", "Integration")]
public class MySqlSchemaReaderTests : IAsyncLifetime
{
    // Binary logging is on by default in the mysql:8.0 image, which requires the SUPER
    // privilege to create a trigger unless it's disabled - not needed in an ephemeral test container.
    private readonly MySqlContainer _container = new MySqlBuilder("mysql:8.0")
        .WithCommand("--disable-log-bin")
        .Build();

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync();

        await using var conn = new MySqlConnection(_container.GetConnectionString());
        await conn.OpenAsync();

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE authors (
                id INT AUTO_INCREMENT PRIMARY KEY,
                name VARCHAR(200) NOT NULL UNIQUE,
                email VARCHAR(200) COMMENT 'Contact address'
            ) COMMENT = 'People who write posts';

            CREATE TABLE posts (
                id INT AUTO_INCREMENT PRIMARY KEY,
                author_id INT NOT NULL,
                title VARCHAR(200) NOT NULL,
                view_count INT NOT NULL DEFAULT 0,
                mood ENUM('happy', 'sad', 'neutral'),
                CONSTRAINT fk_posts_author FOREIGN KEY (author_id) REFERENCES authors(id) ON DELETE CASCADE,
                CONSTRAINT chk_view_count CHECK (view_count >= 0),
                INDEX idx_posts_title (title)
            );

            CREATE TRIGGER posts_touch_updated_at BEFORE UPDATE ON posts FOR EACH ROW SET NEW.title = NEW.title;
            """;
        await cmd.ExecuteNonQueryAsync();
    }

    public async ValueTask DisposeAsync() => await _container.DisposeAsync();

    [Fact]
    public async Task ReadSchemaAsync_reads_tables_columns_and_constraints()
    {
        var reader = new MySqlSchemaReader();
        var database = _container.GetConnectionString().Split(';')
            .Select(p => p.Split('=', 2))
            .Single(p => p[0].Equals("Database", StringComparison.OrdinalIgnoreCase))[1];

        var schemaData = await reader.ReadSchemaAsync(_container.GetConnectionString(), database, CancellationToken.None);

        Assert.Equal(new[] { "authors", "posts" }, schemaData.Tables.Select(t => t.Name));

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

        // InnoDB auto-creates a supporting index for the FK column alongside the explicit one.
        var index = posts.Indexes.Single(i => i.Name == "idx_posts_title");
        Assert.Contains("title", index.Columns);

        var trigger = Assert.Single(posts.Triggers);
        Assert.Equal("posts_touch_updated_at", trigger.Name);
        Assert.Equal("BEFORE", trigger.Timing);
        Assert.True(trigger.IsEnabled);

        var moodColumn = posts.Columns.Single(c => c.Name == "mood");
        Assert.Contains("enum(", moodColumn.DataType);
        Assert.Empty(schemaData.Enums);
    }
}
