using MySqlConnector;
using SchemaFlex.Core.Models;

namespace SchemaFlex.Core.Database.MySql;

public class MySqlSchemaReader : ISchemaReader
{
    public async Task<SchemaData> ReadSchemaAsync(string connectionString, string schema, CancellationToken token)
    {
        await using var conn = new MySqlConnection(connectionString);
        await conn.OpenAsync(token);

        var tableEntries = await GetTableNamesAsync(conn, schema, token);
        var tableNames = tableEntries.Select(t => t.Name).ToList();
        var viewNames = new HashSet<string>(tableEntries.Where(t => t.IsView).Select(t => t.Name), StringComparer.OrdinalIgnoreCase);

        var (primaryKeysByTable, uniqueColumnsByTable) = await GetKeyConstraintColumnsAsync(conn, schema, token);
        var foreignKeysByTable = await GetForeignKeysAsync(conn, schema, token);
        var viewDependenciesByTable = await GetViewDependenciesAsync(conn, schema, token);
        var viewDefinitionsByTable = await GetViewDefinitionsAsync(conn, schema, token);
        var checkConstraintsByTable = await GetCheckConstraintsAsync(conn, schema, token);
        var indexesByTable = await GetIndexesAsync(conn, schema, token);
        var triggersByTable = await GetTriggersAsync(conn, schema, token);
        var tableCommentsByTable = await GetTableCommentsAsync(conn, schema, token);
        var columnCommentsByTable = await GetColumnCommentsAsync(conn, schema, token);
        var columnsByTable = await GetColumnsAsync(conn, schema, primaryKeysByTable, uniqueColumnsByTable, columnCommentsByTable, token);

        var tables = tableNames
            .Select(name => new Table(
                name,
                columnsByTable.TryGetValue(name, out var cols) ? cols : new List<Column>(),
                foreignKeysByTable.TryGetValue(name, out var fks) ? fks : new List<ForeignKey>(),
                checkConstraintsByTable.TryGetValue(name, out var checks) ? checks : new List<CheckConstraint>(),
                indexesByTable.TryGetValue(name, out var indexes) ? indexes : new List<IndexInfo>(),
                triggersByTable.TryGetValue(name, out var triggers) ? triggers : new List<TriggerInfo>(),
                tableCommentsByTable.TryGetValue(name, out var comment) ? comment : null,
                viewNames.Contains(name),
                viewDependenciesByTable.TryGetValue(name, out var deps) ? deps : new List<string>(),
                viewDefinitionsByTable.TryGetValue(name, out var definition) ? definition : null))
            .OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // MySQL/MariaDB enums are inline per-column ("enum('a','b')") rather than reusable
        // named types like Postgres - Enums stays empty and the raw enum(...) text is kept
        // in Column.DataType instead (see GetColumnsAsync).
        return new SchemaData(schema, DateTimeOffset.UtcNow, tables, new List<EnumType>(), DatabaseProvider.MySql);
    }

    private static async Task<List<(string Name, bool IsView)>> GetTableNamesAsync(MySqlConnection conn, string schema, CancellationToken token)
    {
        var tableNames = new List<(string Name, bool IsView)>();

        await using var cmd = new MySqlCommand(
            @"SELECT TABLE_NAME, TABLE_TYPE FROM INFORMATION_SCHEMA.TABLES
              WHERE TABLE_SCHEMA = @schema AND TABLE_TYPE IN ('BASE TABLE', 'VIEW')
              ORDER BY TABLE_NAME;",
            conn);
        cmd.Parameters.AddWithValue("@schema", schema);

        await using var reader = await cmd.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
        {
            var isView = string.Equals(reader.GetString(1), "VIEW", StringComparison.OrdinalIgnoreCase);
            tableNames.Add((reader.GetString(0), isView));
        }

        return tableNames;
    }

    private static async Task<Dictionary<string, List<Column>>> GetColumnsAsync(
        MySqlConnection conn,
        string schema,
        Dictionary<string, HashSet<string>> primaryKeysByTable,
        Dictionary<string, HashSet<string>> uniqueColumnsByTable,
        Dictionary<(string Table, string Column), string> columnCommentsByTable,
        CancellationToken token)
    {
        var result = new Dictionary<string, List<Column>>(StringComparer.OrdinalIgnoreCase);

        // COLUMN_TYPE (unlike the bare DATA_TYPE) already carries length/precision/scale
        // (e.g. "varchar(255)", "decimal(10,2)") and, for enums, the allowed values inline
        // ("enum('a','b')") - exactly the fully-qualified type text DDL generation needs,
        // so it's used as the column type across the board rather than just for enums.
        await using var cmd = new MySqlCommand(
            @"SELECT TABLE_NAME, COLUMN_NAME, COLUMN_TYPE, IS_NULLABLE, COLUMN_DEFAULT, EXTRA, GENERATION_EXPRESSION
              FROM INFORMATION_SCHEMA.COLUMNS
              WHERE TABLE_SCHEMA = @schema
              ORDER BY TABLE_NAME, ORDINAL_POSITION;",
            conn);
        cmd.Parameters.AddWithValue("@schema", schema);

        await using var reader = await cmd.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
        {
            var tableName = reader.GetString(0);
            var columnName = reader.GetString(1);
            var dataType = reader.IsDBNull(2) ? "unknown" : reader.GetString(2);
            var isNullable = !reader.IsDBNull(3) && string.Equals(reader.GetString(3), "YES", StringComparison.OrdinalIgnoreCase);
            var columnDefault = reader.IsDBNull(4) ? null : reader.GetString(4);
            var extra = reader.IsDBNull(5) ? "" : reader.GetString(5);
            var isIdentity = extra.Contains("auto_increment", StringComparison.OrdinalIgnoreCase);
            var isGenerated = extra.Contains("GENERATED", StringComparison.OrdinalIgnoreCase);
            var generationExpression = isGenerated && !reader.IsDBNull(6) ? reader.GetString(6) : null;
            var isPrimaryKey = primaryKeysByTable.TryGetValue(tableName, out var pks) && pks.Contains(columnName);
            var isUnique = uniqueColumnsByTable.TryGetValue(tableName, out var uqs) && uqs.Contains(columnName);
            var comment = columnCommentsByTable.TryGetValue((tableName, columnName), out var c) ? c : null;

            if (!result.TryGetValue(tableName, out var list))
            {
                list = new List<Column>();
                result[tableName] = list;
            }

            list.Add(new Column(columnName, dataType, isNullable, isPrimaryKey, isUnique, columnDefault, isIdentity, generationExpression, comment));
        }

        return result;
    }

    private static async Task<(Dictionary<string, HashSet<string>> PrimaryKeys, Dictionary<string, HashSet<string>> Unique)> GetKeyConstraintColumnsAsync(
        MySqlConnection conn, string schema, CancellationToken token)
    {
        var primaryKeys = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var unique = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        await using var cmd = new MySqlCommand(
            @"SELECT tc.TABLE_NAME, kcu.COLUMN_NAME, tc.CONSTRAINT_TYPE
              FROM INFORMATION_SCHEMA.TABLE_CONSTRAINTS tc
              JOIN INFORMATION_SCHEMA.KEY_COLUMN_USAGE kcu
                ON tc.CONSTRAINT_NAME = kcu.CONSTRAINT_NAME AND tc.TABLE_SCHEMA = kcu.TABLE_SCHEMA AND tc.TABLE_NAME = kcu.TABLE_NAME
              WHERE tc.CONSTRAINT_TYPE IN ('PRIMARY KEY', 'UNIQUE') AND tc.TABLE_SCHEMA = @schema;",
            conn);
        cmd.Parameters.AddWithValue("@schema", schema);

        await using var reader = await cmd.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
        {
            var tableName = reader.GetString(0);
            var columnName = reader.GetString(1);
            var type = reader.GetString(2);

            var target = string.Equals(type, "PRIMARY KEY", StringComparison.OrdinalIgnoreCase) ? primaryKeys : unique;
            if (!target.TryGetValue(tableName, out var set))
            {
                set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                target[tableName] = set;
            }

            set.Add(columnName);
        }

        return (primaryKeys, unique);
    }

    private static async Task<Dictionary<string, List<ForeignKey>>> GetForeignKeysAsync(MySqlConnection conn, string schema, CancellationToken token)
    {
        var result = new Dictionary<string, List<ForeignKey>>(StringComparer.OrdinalIgnoreCase);

        await using var cmd = new MySqlCommand(
            @"SELECT kcu.CONSTRAINT_NAME, kcu.TABLE_NAME, kcu.COLUMN_NAME, kcu.REFERENCED_TABLE_NAME, kcu.REFERENCED_COLUMN_NAME,
                     rc.UPDATE_RULE, rc.DELETE_RULE
              FROM INFORMATION_SCHEMA.KEY_COLUMN_USAGE kcu
              JOIN INFORMATION_SCHEMA.REFERENTIAL_CONSTRAINTS rc
                ON rc.CONSTRAINT_NAME = kcu.CONSTRAINT_NAME AND rc.CONSTRAINT_SCHEMA = kcu.TABLE_SCHEMA
              WHERE kcu.TABLE_SCHEMA = @schema AND kcu.REFERENCED_TABLE_NAME IS NOT NULL;",
            conn);
        cmd.Parameters.AddWithValue("@schema", schema);

        await using var reader = await cmd.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
        {
            var constraintName = reader.GetString(0);
            var tableName = reader.GetString(1);
            var columnName = reader.GetString(2);
            var refTable = reader.GetString(3);
            var refColumn = reader.GetString(4);
            var onUpdate = reader.IsDBNull(5) ? null : reader.GetString(5);
            var onDelete = reader.IsDBNull(6) ? null : reader.GetString(6);

            if (!result.TryGetValue(tableName, out var list))
            {
                list = new List<ForeignKey>();
                result[tableName] = list;
            }

            list.Add(new ForeignKey(constraintName, columnName, refTable, refColumn, onDelete, onUpdate));
        }

        return result;
    }

    private static async Task<Dictionary<string, List<string>>> GetViewDependenciesAsync(MySqlConnection conn, string schema, CancellationToken token)
    {
        var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        // view_table_usage is derived by MySQL from the parsed view definition, so it
        // already resolves straight through any CTEs in the view body down to the real
        // tables (and other views) actually touched - no text parsing needed here.
        await using var cmd = new MySqlCommand(
            @"SELECT VIEW_NAME, TABLE_NAME
              FROM INFORMATION_SCHEMA.VIEW_TABLE_USAGE
              WHERE VIEW_SCHEMA = @schema AND TABLE_NAME <> VIEW_NAME;",
            conn);
        cmd.Parameters.AddWithValue("@schema", schema);

        await using var reader = await cmd.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
        {
            var viewName = reader.GetString(0);
            var refName = reader.GetString(1);

            if (!result.TryGetValue(viewName, out var list))
            {
                list = new List<string>();
                result[viewName] = list;
            }

            if (!list.Contains(refName, StringComparer.OrdinalIgnoreCase)) list.Add(refName);
        }

        return result;
    }

    private static async Task<Dictionary<string, string>> GetViewDefinitionsAsync(MySqlConnection conn, string schema, CancellationToken token)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // VIEW_DEFINITION only holds the SELECT body, not the wrapping CREATE VIEW -
        // unlike SQL Server's sys.sql_modules.definition, which carries the original
        // statement verbatim - so the CREATE VIEW clause is rebuilt here instead.
        await using var cmd = new MySqlCommand(
            @"SELECT TABLE_NAME, VIEW_DEFINITION
              FROM INFORMATION_SCHEMA.VIEWS
              WHERE TABLE_SCHEMA = @schema;",
            conn);
        cmd.Parameters.AddWithValue("@schema", schema);

        await using var reader = await cmd.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
        {
            var viewName = reader.GetString(0);
            if (reader.IsDBNull(1)) continue;
            var body = reader.GetString(1).TrimEnd('\n', '\r', ' ', '\t', ';');
            result[viewName] = "CREATE VIEW `" + viewName + "` AS\n" + body + ";";
        }

        return result;
    }

    private static async Task<Dictionary<string, List<CheckConstraint>>> GetCheckConstraintsAsync(MySqlConnection conn, string schema, CancellationToken token)
    {
        var result = new Dictionary<string, List<CheckConstraint>>(StringComparer.OrdinalIgnoreCase);

        await using var cmd = new MySqlCommand(
            @"SELECT tc.TABLE_NAME, cc.CONSTRAINT_NAME, cc.CHECK_CLAUSE
              FROM INFORMATION_SCHEMA.CHECK_CONSTRAINTS cc
              JOIN INFORMATION_SCHEMA.TABLE_CONSTRAINTS tc
                ON tc.CONSTRAINT_NAME = cc.CONSTRAINT_NAME AND tc.CONSTRAINT_SCHEMA = cc.CONSTRAINT_SCHEMA
              WHERE cc.CONSTRAINT_SCHEMA = @schema;",
            conn);
        cmd.Parameters.AddWithValue("@schema", schema);

        await using var reader = await cmd.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
        {
            var tableName = reader.GetString(0);
            var name = reader.GetString(1);
            var definition = reader.IsDBNull(2) ? null : reader.GetString(2);
            if (definition == null) continue;

            if (!result.TryGetValue(tableName, out var list))
            {
                list = new List<CheckConstraint>();
                result[tableName] = list;
            }

            list.Add(new CheckConstraint(name, definition));
        }

        return result;
    }

    private static async Task<Dictionary<string, List<IndexInfo>>> GetIndexesAsync(MySqlConnection conn, string schema, CancellationToken token)
    {
        // information_schema.statistics has one row per column-in-index - grouped back into
        // one IndexInfo per (table, index name), columns ordered by SEQ_IN_INDEX. The
        // PRIMARY index is excluded - the PK badge on each column already says that.
        var order = new List<(string Table, string Name)>();
        var byKey = new Dictionary<(string Table, string Name), (bool IsUnique, string Method, List<string> Columns)>();

        await using var cmd = new MySqlCommand(
            @"SELECT TABLE_NAME, INDEX_NAME, NON_UNIQUE, INDEX_TYPE, COLUMN_NAME
              FROM INFORMATION_SCHEMA.STATISTICS
              WHERE TABLE_SCHEMA = @schema AND INDEX_NAME <> 'PRIMARY'
              ORDER BY TABLE_NAME, INDEX_NAME, SEQ_IN_INDEX;",
            conn);
        cmd.Parameters.AddWithValue("@schema", schema);

        await using var reader = await cmd.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
        {
            var tableName = reader.GetString(0);
            var indexName = reader.GetString(1);
            var nonUnique = reader.GetInt32(2) != 0;
            var method = reader.IsDBNull(3) ? "" : reader.GetString(3);
            var columnName = reader.GetString(4);

            var key = (tableName, indexName);
            if (!byKey.TryGetValue(key, out var entry))
            {
                entry = (!nonUnique, method, new List<string>());
                byKey[key] = entry;
                order.Add(key);
            }

            entry.Columns.Add(columnName);
        }

        var result = new Dictionary<string, List<IndexInfo>>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in order)
        {
            var entry = byKey[key];

            if (!result.TryGetValue(key.Table, out var list))
            {
                list = new List<IndexInfo>();
                result[key.Table] = list;
            }

            list.Add(new IndexInfo(key.Name, entry.Columns, entry.IsUnique, entry.Method));
        }

        return result;
    }

    private static async Task<Dictionary<string, List<TriggerInfo>>> GetTriggersAsync(MySqlConnection conn, string schema, CancellationToken token)
    {
        var result = new Dictionary<string, List<TriggerInfo>>(StringComparer.OrdinalIgnoreCase);

        await using var cmd = new MySqlCommand(
            @"SELECT EVENT_OBJECT_TABLE, TRIGGER_NAME, ACTION_TIMING, ACTION_ORIENTATION, EVENT_MANIPULATION, ACTION_STATEMENT
              FROM INFORMATION_SCHEMA.TRIGGERS
              WHERE TRIGGER_SCHEMA = @schema
              ORDER BY EVENT_OBJECT_TABLE, TRIGGER_NAME;",
            conn);
        cmd.Parameters.AddWithValue("@schema", schema);

        await using var reader = await cmd.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
        {
            var tableName = reader.GetString(0);
            var triggerName = reader.GetString(1);
            var timing = reader.IsDBNull(2) ? "" : reader.GetString(2);
            var level = reader.IsDBNull(3) ? "" : reader.GetString(3);
            var eventManipulation = reader.IsDBNull(4) ? "" : reader.GetString(4);
            var statement = reader.IsDBNull(5) ? "" : reader.GetString(5);

            if (!result.TryGetValue(tableName, out var list))
            {
                list = new List<TriggerInfo>();
                result[tableName] = list;
            }

            // MySQL/MariaDB has no ENABLE/DISABLE TRIGGER statement - a trigger that exists
            // is always enabled.
            list.Add(new TriggerInfo(triggerName, timing, level, new List<string> { eventManipulation }, statement, true));
        }

        return result;
    }

    private static async Task<Dictionary<string, string>> GetTableCommentsAsync(MySqlConnection conn, string schema, CancellationToken token)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        await using var cmd = new MySqlCommand(
            @"SELECT TABLE_NAME, TABLE_COMMENT
              FROM INFORMATION_SCHEMA.TABLES
              WHERE TABLE_SCHEMA = @schema AND TABLE_COMMENT <> '';",
            conn);
        cmd.Parameters.AddWithValue("@schema", schema);

        await using var reader = await cmd.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
        {
            result[reader.GetString(0)] = reader.GetString(1);
        }

        return result;
    }

    private static async Task<Dictionary<(string Table, string Column), string>> GetColumnCommentsAsync(MySqlConnection conn, string schema, CancellationToken token)
    {
        var result = new Dictionary<(string, string), string>();

        await using var cmd = new MySqlCommand(
            @"SELECT TABLE_NAME, COLUMN_NAME, COLUMN_COMMENT
              FROM INFORMATION_SCHEMA.COLUMNS
              WHERE TABLE_SCHEMA = @schema AND COLUMN_COMMENT <> '';",
            conn);
        cmd.Parameters.AddWithValue("@schema", schema);

        await using var reader = await cmd.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
        {
            result[(reader.GetString(0), reader.GetString(1))] = reader.GetString(2);
        }

        return result;
    }
}
