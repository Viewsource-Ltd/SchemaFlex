using Npgsql;
using SchemaFlex.Core.Database.Postgres;
using Testcontainers.PostgreSql;
using Xunit;

namespace SchemaFlex.Tests.Integration;

[Trait("Category", "Integration")]
public class PostgresSchemaReaderTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:16-alpine").Build();

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync();

        await using var conn = new NpgsqlConnection(_container.GetConnectionString());
        await conn.OpenAsync();

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TYPE mood AS ENUM ('happy', 'sad', 'neutral');

            CREATE TABLE authors (
                id SERIAL PRIMARY KEY,
                name TEXT NOT NULL UNIQUE,
                email TEXT
            );
            COMMENT ON TABLE authors IS 'People who write posts';
            COMMENT ON COLUMN authors.email IS 'Contact address';

            CREATE TABLE posts (
                id SERIAL PRIMARY KEY,
                author_id INTEGER NOT NULL REFERENCES authors(id) ON DELETE CASCADE,
                title TEXT NOT NULL,
                view_count INTEGER NOT NULL DEFAULT 0 CHECK (view_count >= 0),
                current_mood mood
            );
            CREATE INDEX idx_posts_title ON posts(title);

            CREATE FUNCTION touch_updated_at() RETURNS trigger AS $$
            BEGIN
                RETURN NEW;
            END;
            $$ LANGUAGE plpgsql;

            CREATE TRIGGER posts_touch_updated_at
                BEFORE UPDATE ON posts
                FOR EACH ROW
                EXECUTE FUNCTION touch_updated_at();
            """;
        await cmd.ExecuteNonQueryAsync();
    }

    public async ValueTask DisposeAsync() => await _container.DisposeAsync();

    [Fact]
    public async Task ReadSchemaAsync_reads_tables_columns_and_constraints()
    {
        var reader = new PostgresSchemaReader();

        var schemaData = await reader.ReadSchemaAsync(_container.GetConnectionString(), "public", CancellationToken.None);

        Assert.Equal(new[] { "authors", "posts" }, schemaData.Tables.Select(t => t.Name));

        var authors = schemaData.Tables.Single(t => t.Name == "authors");
        Assert.Equal("People who write posts", authors.Comment);
        var idColumn = authors.Columns.Single(c => c.Name == "id");
        Assert.True(idColumn.IsPrimaryKey);
        Assert.True(idColumn.IsIdentity || idColumn.Default != null);
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
        Assert.Equal("BEFORE", trigger.Timing);
        Assert.True(trigger.IsEnabled);

        var moodEnum = Assert.Single(schemaData.Enums);
        Assert.Equal("mood", moodEnum.Name);
        Assert.Equal(new[] { "happy", "sad", "neutral" }, moodEnum.Values);
    }
}
