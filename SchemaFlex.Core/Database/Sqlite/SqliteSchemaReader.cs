using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using SchemaFlex.Core.Models;

namespace SchemaFlex.Core.Database.Sqlite;

public class SqliteSchemaReader : ISchemaReader
{
    public async Task<SchemaData> ReadSchemaAsync(string connectionString, string schema, CancellationToken token)
    {
        // SQLite has no schema namespace of its own - "schema" here is the name of an
        // attached database ("main" for the primary file, or whatever ATTACH DATABASE
        // assigned), used to qualify sqlite_master/pragma lookups below.
        var schemaIdentifier = ValidateSchemaIdentifier(schema);

        await using var conn = new SqliteConnection(connectionString);
        await conn.OpenAsync(token);

        var tableDefinitions = await GetTableDefinitionsAsync(conn, schemaIdentifier, token);
        var tableNames = tableDefinitions.Keys.ToList();
        var viewNames = await GetViewNamesAsync(conn, schemaIdentifier, token);

        var checkConstraintsByTable = tableDefinitions.ToDictionary(
            kv => kv.Key,
            kv => ExtractCheckConstraints(kv.Value),
            StringComparer.OrdinalIgnoreCase);

        var columnsByTable = new Dictionary<string, List<Column>>(StringComparer.OrdinalIgnoreCase);
        var foreignKeysByTable = new Dictionary<string, List<ForeignKey>>(StringComparer.OrdinalIgnoreCase);
        var indexesByTable = new Dictionary<string, List<IndexInfo>>(StringComparer.OrdinalIgnoreCase);

        foreach (var tableName in tableNames)
        {
            var uniqueColumns = await GetUniqueColumnsAsync(conn, schemaIdentifier, tableName, token);
            columnsByTable[tableName] = await GetColumnsAsync(conn, schemaIdentifier, tableName, uniqueColumns, tableDefinitions[tableName], token);
            foreignKeysByTable[tableName] = await GetForeignKeysAsync(conn, schemaIdentifier, tableName, token);
            indexesByTable[tableName] = await GetIndexesAsync(conn, schemaIdentifier, tableName, token);
        }

        var triggersByTable = await GetTriggersAsync(conn, schemaIdentifier, token);

        var tables = tableNames
            .Select(name => new Table(
                name,
                columnsByTable.TryGetValue(name, out var cols) ? cols : new List<Column>(),
                foreignKeysByTable.TryGetValue(name, out var fks) ? fks : new List<ForeignKey>(),
                checkConstraintsByTable.TryGetValue(name, out var checks) ? checks : new List<CheckConstraint>(),
                indexesByTable.TryGetValue(name, out var indexes) ? indexes : new List<IndexInfo>(),
                triggersByTable.TryGetValue(name, out var triggers) ? triggers : new List<TriggerInfo>(),
                // SQLite has no native table/column comment feature - CREATE TABLE text
                // can carry SQL comments, but there's no reliable way to attribute one to
                // the table itself, so this stays null rather than guessing.
                null,
                viewNames.Contains(name)))
            .OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // SQLite has no reusable named enum type - CHECK(col IN (...)) is the closest
        // idiom, and that's already surfaced as a check constraint above.
        return new SchemaData(schema, DateTimeOffset.UtcNow, tables, new List<EnumType>(), DatabaseProvider.Sqlite);
    }

    private static string ValidateSchemaIdentifier(string schema)
    {
        if (string.IsNullOrWhiteSpace(schema) || !Regex.IsMatch(schema, "^[A-Za-z_][A-Za-z0-9_]*$"))
        {
            throw new ArgumentException(
                $"Invalid SQLite schema/database name '{schema}'. Expected an attached database name such as 'main'.",
                nameof(schema));
        }

        return schema;
    }

    private static async Task<Dictionary<string, string>> GetTableDefinitionsAsync(SqliteConnection conn, string schema, CancellationToken token)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT name, sql FROM {Quote(schema)}.sqlite_master " +
                           "WHERE type IN ('table', 'view') AND name NOT LIKE 'sqlite_%' ORDER BY name;";

        await using var reader = await cmd.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
        {
            var name = reader.GetString(0);
            var sql = reader.IsDBNull(1) ? "" : reader.GetString(1);
            result[name] = sql;
        }

        return result;
    }

    private static async Task<HashSet<string>> GetViewNamesAsync(SqliteConnection conn, string schema, CancellationToken token)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT name FROM {Quote(schema)}.sqlite_master WHERE type = 'view';";

        await using var reader = await cmd.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
        {
            result.Add(reader.GetString(0));
        }

        return result;
    }

    private static async Task<List<Column>> GetColumnsAsync(
        SqliteConnection conn, string schema, string tableName, HashSet<string> uniqueColumns, string tableSql, CancellationToken token)
    {
        var columns = new List<Column>();

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"PRAGMA {Quote(schema)}.table_info({QuoteLiteral(tableName)});";

        await using var reader = await cmd.ExecuteReaderAsync(token);

        var rows = new List<(string Name, string Type, bool NotNull, string? Default, int Pk)>();
        while (await reader.ReadAsync(token))
        {
            rows.Add((
                reader.GetString(reader.GetOrdinal("name")),
                reader.IsDBNull(reader.GetOrdinal("type")) ? "" : reader.GetString(reader.GetOrdinal("type")),
                reader.GetInt32(reader.GetOrdinal("notnull")) != 0,
                reader.IsDBNull(reader.GetOrdinal("dflt_value")) ? null : reader.GetString(reader.GetOrdinal("dflt_value")),
                reader.GetInt32(reader.GetOrdinal("pk"))));
        }

        // A single-column INTEGER PRIMARY KEY is a rowid alias in SQLite - it always
        // behaves as an auto-incrementing identity, whether or not AUTOINCREMENT is
        // declared, so that (rather than the keyword) is what drives IsIdentity here.
        var pkColumnCount = rows.Count(r => r.Pk > 0);

        foreach (var row in rows)
        {
            var dataType = string.IsNullOrEmpty(row.Type) ? "unknown" : row.Type;
            var isIdentity = row.Pk > 0 && pkColumnCount == 1 && string.Equals(row.Type, "INTEGER", StringComparison.OrdinalIgnoreCase);
            var isUnique = uniqueColumns.Contains(row.Name);
            var generatedExpression = ExtractGeneratedExpression(tableSql, row.Name);

            columns.Add(new Column(
                row.Name,
                dataType,
                !row.NotNull,
                row.Pk > 0,
                isUnique,
                row.Default,
                isIdentity,
                generatedExpression,
                null));
        }

        return columns;
    }

    private static async Task<HashSet<string>> GetUniqueColumnsAsync(SqliteConnection conn, string schema, string tableName, CancellationToken token)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        await using var listCmd = conn.CreateCommand();
        listCmd.CommandText = $"PRAGMA {Quote(schema)}.index_list({QuoteLiteral(tableName)});";

        var uniqueIndexNames = new List<string>();
        await using (var reader = await listCmd.ExecuteReaderAsync(token))
        {
            while (await reader.ReadAsync(token))
            {
                var isUnique = reader.GetInt32(reader.GetOrdinal("unique")) != 0;
                var origin = reader.GetString(reader.GetOrdinal("origin"));
                // "pk" origin is the implicit index backing a primary key - already
                // captured via table_info's pk column, so skip it here.
                if (isUnique && !string.Equals(origin, "pk", StringComparison.OrdinalIgnoreCase))
                {
                    uniqueIndexNames.Add(reader.GetString(reader.GetOrdinal("name")));
                }
            }
        }

        foreach (var indexName in uniqueIndexNames)
        {
            await using var infoCmd = conn.CreateCommand();
            infoCmd.CommandText = $"PRAGMA {Quote(schema)}.index_info({QuoteLiteral(indexName)});";

            await using var infoReader = await infoCmd.ExecuteReaderAsync(token);
            while (await infoReader.ReadAsync(token))
            {
                result.Add(infoReader.GetString(infoReader.GetOrdinal("name")));
            }
        }

        return result;
    }

    private static async Task<List<ForeignKey>> GetForeignKeysAsync(SqliteConnection conn, string schema, string tableName, CancellationToken token)
    {
        var result = new List<ForeignKey>();

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"PRAGMA {Quote(schema)}.foreign_key_list({QuoteLiteral(tableName)});";

        await using var reader = await cmd.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
        {
            var id = reader.GetInt32(reader.GetOrdinal("id"));
            var refTable = reader.GetString(reader.GetOrdinal("table"));
            var fromColumn = reader.GetString(reader.GetOrdinal("from"));
            var toColumn = reader.GetString(reader.GetOrdinal("to"));
            var onUpdate = reader.IsDBNull(reader.GetOrdinal("on_update")) ? null : reader.GetString(reader.GetOrdinal("on_update"));
            var onDelete = reader.IsDBNull(reader.GetOrdinal("on_delete")) ? null : reader.GetString(reader.GetOrdinal("on_delete"));

            // SQLite foreign keys aren't named - synthesize a stable, per-table identifier
            // from the pragma's own "id" grouping instead.
            result.Add(new ForeignKey($"fk_{tableName}_{id}", fromColumn, refTable, toColumn, NormalizeAction(onDelete), NormalizeAction(onUpdate)));
        }

        return result;
    }

    private static string? NormalizeAction(string? action) =>
        string.IsNullOrEmpty(action) || string.Equals(action, "NO ACTION", StringComparison.OrdinalIgnoreCase) ? null : action;

    private static async Task<List<IndexInfo>> GetIndexesAsync(SqliteConnection conn, string schema, string tableName, CancellationToken token)
    {
        var result = new List<IndexInfo>();

        await using var listCmd = conn.CreateCommand();
        listCmd.CommandText = $"PRAGMA {Quote(schema)}.index_list({QuoteLiteral(tableName)});";

        var indexes = new List<(string Name, bool IsUnique, string Origin)>();
        await using (var reader = await listCmd.ExecuteReaderAsync(token))
        {
            while (await reader.ReadAsync(token))
            {
                indexes.Add((
                    reader.GetString(reader.GetOrdinal("name")),
                    reader.GetInt32(reader.GetOrdinal("unique")) != 0,
                    reader.GetString(reader.GetOrdinal("origin"))));
            }
        }

        foreach (var index in indexes)
        {
            // The implicit index backing the primary key is excluded - the PK badge on
            // each column already conveys that.
            if (string.Equals(index.Origin, "pk", StringComparison.OrdinalIgnoreCase)) continue;

            await using var infoCmd = conn.CreateCommand();
            infoCmd.CommandText = $"PRAGMA {Quote(schema)}.index_info({QuoteLiteral(index.Name)});";

            var columns = new List<string>();
            await using (var infoReader = await infoCmd.ExecuteReaderAsync(token))
            {
                while (await infoReader.ReadAsync(token))
                {
                    columns.Add(infoReader.GetString(infoReader.GetOrdinal("name")));
                }
            }

            // SQLite indexes are always B-tree - there's no separate access-method concept
            // to report like Postgres' amname or MySQL's INDEX_TYPE.
            result.Add(new IndexInfo(index.Name, columns, index.IsUnique, "btree"));
        }

        return result;
    }

    private static async Task<Dictionary<string, List<TriggerInfo>>> GetTriggersAsync(SqliteConnection conn, string schema, CancellationToken token)
    {
        var result = new Dictionary<string, List<TriggerInfo>>(StringComparer.OrdinalIgnoreCase);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT tbl_name, name, sql FROM {Quote(schema)}.sqlite_master WHERE type = 'trigger' ORDER BY tbl_name, name;";

        await using var reader = await cmd.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
        {
            var tableName = reader.GetString(0);
            var triggerName = reader.GetString(1);
            var sql = reader.IsDBNull(2) ? "" : reader.GetString(2);

            var timingMatch = Regex.Match(sql, @"\b(BEFORE|AFTER|INSTEAD\s+OF)\b", RegexOptions.IgnoreCase);
            var timing = timingMatch.Success ? timingMatch.Value.ToUpperInvariant() : "";

            var events = new List<string>();
            foreach (Match m in Regex.Matches(sql, @"\b(INSERT|UPDATE|DELETE)\b", RegexOptions.IgnoreCase))
            {
                var evt = m.Value.ToUpperInvariant();
                if (!events.Contains(evt)) events.Add(evt);
            }

            if (!result.TryGetValue(tableName, out var list))
            {
                list = new List<TriggerInfo>();
                result[tableName] = list;
            }

            // SQLite triggers only ever fire per-row, and there's no ENABLE/DISABLE
            // TRIGGER statement - a trigger that exists is always enabled.
            list.Add(new TriggerInfo(triggerName, timing, "ROW", events, sql, true));
        }

        return result;
    }

    private static List<CheckConstraint> ExtractCheckConstraints(string tableSql)
    {
        var result = new List<CheckConstraint>();
        var anonymousCount = 0;

        foreach (Match match in Regex.Matches(tableSql, @"\bCHECK\s*\(", RegexOptions.IgnoreCase))
        {
            var openParenIndex = match.Index + match.Length - 1;
            var expression = ExtractBalancedParens(tableSql, openParenIndex);
            if (expression == null) continue;

            var precedingText = tableSql[..match.Index];
            var nameMatch = Regex.Match(precedingText, @"CONSTRAINT\s+(""[^""]+""|`[^`]+`|\[[^\]]+\]|\w+)\s*$", RegexOptions.IgnoreCase);
            var name = nameMatch.Success ? nameMatch.Groups[1].Value.Trim('"', '`', '[', ']') : $"check_{++anonymousCount}";

            result.Add(new CheckConstraint(name, expression));
        }

        return result;
    }

    private static string? ExtractGeneratedExpression(string tableSql, string columnName)
    {
        var columnPattern = $@"(?:""{Regex.Escape(columnName)}""|`{Regex.Escape(columnName)}`|\[{Regex.Escape(columnName)}\]|{Regex.Escape(columnName)})\s+[^,()]*?\bGENERATED\s+ALWAYS\s+AS\s*\(";
        var match = Regex.Match(tableSql, columnPattern, RegexOptions.IgnoreCase);
        if (!match.Success) return null;

        var openParenIndex = match.Index + match.Length - 1;
        return ExtractBalancedParens(tableSql, openParenIndex);
    }

    private static string? ExtractBalancedParens(string text, int openParenIndex)
    {
        if (openParenIndex < 0 || openParenIndex >= text.Length || text[openParenIndex] != '(') return null;

        var depth = 0;
        for (var i = openParenIndex; i < text.Length; i++)
        {
            if (text[i] == '(') depth++;
            else if (text[i] == ')')
            {
                depth--;
                if (depth == 0) return text[(openParenIndex + 1)..i].Trim();
            }
        }

        return null;
    }

    private static string Quote(string identifier) => $"\"{identifier.Replace("\"", "\"\"")}\"";

    private static string QuoteLiteral(string value) => $"'{value.Replace("'", "''")}'";
}
