using Microsoft.Data.SqlClient;
using SchemaFlex.Core.Models;

namespace SchemaFlex.Core.Database.SqlServer;

public class SqlServerSchemaReader : ISchemaReader
{
    public async Task<SchemaData> ReadSchemaAsync(string connectionString, string schema, CancellationToken token)
    {
        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync(token);

        var tableEntries = await GetTableNamesAsync(conn, schema, token);
        var tableNames = tableEntries.Select(t => t.Name).ToList();
        var viewNames = new HashSet<string>(tableEntries.Where(t => t.IsView).Select(t => t.Name), StringComparer.OrdinalIgnoreCase);

        var (primaryKeysByTable, uniqueColumnsByTable) = await GetKeyConstraintColumnsAsync(conn, schema, token);
        var foreignKeysByTable = await GetForeignKeysAsync(conn, schema, token);
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
                viewNames.Contains(name)))
            .OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // SQL Server has no reusable named enum type like Postgres - Enums is always empty.
        return new SchemaData(schema, DateTimeOffset.UtcNow, tables, new List<EnumType>(), DatabaseProvider.SqlServer);
    }

    private static async Task<List<(string Name, bool IsView)>> GetTableNamesAsync(SqlConnection conn, string schema, CancellationToken token)
    {
        var tableNames = new List<(string Name, bool IsView)>();

        await using var cmd = new SqlCommand(
            @"SELECT o.name, CASE WHEN o.type = 'V' THEN 1 ELSE 0 END AS is_view
              FROM sys.objects o
              JOIN sys.schemas s ON s.schema_id = o.schema_id
              WHERE s.name = @schema AND o.type IN ('U', 'V') AND o.is_ms_shipped = 0
              ORDER BY o.name;",
            conn);
        cmd.Parameters.AddWithValue("@schema", schema);

        await using var reader = await cmd.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
        {
            tableNames.Add((reader.GetString(0), reader.GetInt32(1) != 0));
        }

        return tableNames;
    }

    private static async Task<Dictionary<string, List<Column>>> GetColumnsAsync(
        SqlConnection conn,
        string schema,
        Dictionary<string, HashSet<string>> primaryKeysByTable,
        Dictionary<string, HashSet<string>> uniqueColumnsByTable,
        Dictionary<(string Table, string Column), string> columnCommentsByTable,
        CancellationToken token)
    {
        var result = new Dictionary<string, List<Column>>(StringComparer.OrdinalIgnoreCase);

        // sys.columns only carries the bare type name (ty.name) - max_length/precision/scale
        // are separate columns that need formatting back on per type family: char/binary
        // types store max_length in bytes (nvarchar/nchar store it doubled, for UTF-16), -1
        // means MAX, decimal/numeric use precision+scale, and the sub-second time types use
        // just scale. Everything else (int, bit, date, uniqueidentifier, ...) has no size.
        await using var cmd = new SqlCommand(
            @"SELECT t.name AS table_name, c.name AS column_name,
                     CASE
                         WHEN ty.name IN ('varchar', 'char', 'varbinary', 'binary')
                             THEN ty.name + '(' + (CASE WHEN c.max_length = -1 THEN 'MAX' ELSE CAST(c.max_length AS varchar(10)) END) + ')'
                         WHEN ty.name IN ('nvarchar', 'nchar')
                             THEN ty.name + '(' + (CASE WHEN c.max_length = -1 THEN 'MAX' ELSE CAST(c.max_length / 2 AS varchar(10)) END) + ')'
                         WHEN ty.name IN ('decimal', 'numeric')
                             THEN ty.name + '(' + CAST(c.precision AS varchar(10)) + ',' + CAST(c.scale AS varchar(10)) + ')'
                         WHEN ty.name IN ('datetime2', 'time', 'datetimeoffset')
                             THEN ty.name + '(' + CAST(c.scale AS varchar(10)) + ')'
                         ELSE ty.name
                     END AS data_type,
                     c.is_nullable, c.is_identity, dc.definition AS column_default,
                     cc.definition AS generation_expression
              FROM sys.columns c
              JOIN sys.objects t ON t.object_id = c.object_id AND t.type IN ('U', 'V')
              JOIN sys.schemas s ON s.schema_id = t.schema_id
              JOIN sys.types ty ON ty.user_type_id = c.user_type_id
              LEFT JOIN sys.default_constraints dc ON dc.object_id = c.default_object_id
              LEFT JOIN sys.computed_columns cc ON cc.object_id = c.object_id AND cc.column_id = c.column_id
              WHERE s.name = @schema
              ORDER BY t.name, c.column_id;",
            conn);
        cmd.Parameters.AddWithValue("@schema", schema);

        await using var reader = await cmd.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
        {
            var tableName = reader.GetString(0);
            var columnName = reader.GetString(1);
            var dataType = reader.IsDBNull(2) ? "unknown" : reader.GetString(2);
            var isNullable = !reader.IsDBNull(3) && reader.GetBoolean(3);
            var isIdentity = !reader.IsDBNull(4) && reader.GetBoolean(4);
            var columnDefault = reader.IsDBNull(5) ? null : reader.GetString(5);
            var generationExpression = reader.IsDBNull(6) ? null : reader.GetString(6);
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
        SqlConnection conn, string schema, CancellationToken token)
    {
        var primaryKeys = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var unique = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        await using var cmd = new SqlCommand(
            @"SELECT t.name AS table_name, c.name AS column_name, kc.type
              FROM sys.key_constraints kc
              JOIN sys.tables t ON t.object_id = kc.parent_object_id
              JOIN sys.schemas s ON s.schema_id = t.schema_id
              JOIN sys.index_columns ic ON ic.object_id = kc.parent_object_id AND ic.index_id = kc.unique_index_id
              JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
              WHERE s.name = @schema AND kc.type IN ('PK', 'UQ');",
            conn);
        cmd.Parameters.AddWithValue("@schema", schema);

        await using var reader = await cmd.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
        {
            var tableName = reader.GetString(0);
            var columnName = reader.GetString(1);
            var type = reader.GetString(2).Trim();

            var target = type == "PK" ? primaryKeys : unique;
            if (!target.TryGetValue(tableName, out var set))
            {
                set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                target[tableName] = set;
            }

            set.Add(columnName);
        }

        return (primaryKeys, unique);
    }

    private static async Task<Dictionary<string, List<ForeignKey>>> GetForeignKeysAsync(SqlConnection conn, string schema, CancellationToken token)
    {
        var result = new Dictionary<string, List<ForeignKey>>(StringComparer.OrdinalIgnoreCase);

        await using var cmd = new SqlCommand(
            @"SELECT fk.name AS constraint_name, tp.name AS table_name, cp.name AS column_name,
                     tr.name AS ref_table, cr.name AS ref_column,
                     fk.delete_referential_action_desc, fk.update_referential_action_desc
              FROM sys.foreign_keys fk
              JOIN sys.foreign_key_columns fkc ON fkc.constraint_object_id = fk.object_id
              JOIN sys.tables tp ON tp.object_id = fkc.parent_object_id
              JOIN sys.columns cp ON cp.object_id = tp.object_id AND cp.column_id = fkc.parent_column_id
              JOIN sys.tables tr ON tr.object_id = fkc.referenced_object_id
              JOIN sys.columns cr ON cr.object_id = tr.object_id AND cr.column_id = fkc.referenced_column_id
              JOIN sys.schemas s ON s.schema_id = tp.schema_id
              WHERE s.name = @schema;",
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
            var onDelete = reader.IsDBNull(5) ? null : reader.GetString(5);
            var onUpdate = reader.IsDBNull(6) ? null : reader.GetString(6);

            if (!result.TryGetValue(tableName, out var list))
            {
                list = new List<ForeignKey>();
                result[tableName] = list;
            }

            list.Add(new ForeignKey(constraintName, columnName, refTable, refColumn, onDelete, onUpdate));
        }

        return result;
    }

    private static async Task<Dictionary<string, List<CheckConstraint>>> GetCheckConstraintsAsync(SqlConnection conn, string schema, CancellationToken token)
    {
        var result = new Dictionary<string, List<CheckConstraint>>(StringComparer.OrdinalIgnoreCase);

        await using var cmd = new SqlCommand(
            @"SELECT t.name AS table_name, cc.name, cc.definition
              FROM sys.check_constraints cc
              JOIN sys.tables t ON t.object_id = cc.parent_object_id
              JOIN sys.schemas s ON s.schema_id = t.schema_id
              WHERE s.name = @schema;",
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

    private static async Task<Dictionary<string, List<IndexInfo>>> GetIndexesAsync(SqlConnection conn, string schema, CancellationToken token)
    {
        var result = new Dictionary<string, List<IndexInfo>>(StringComparer.OrdinalIgnoreCase);

        // Primary-key-backing indexes are excluded - the PK badge on each column already says
        // that, matching the Postgres reader's behavior.
        await using var cmd = new SqlCommand(
            @"SELECT t.name AS table_name, i.name AS index_name, i.is_unique, i.type_desc,
                     STRING_AGG(c.name, ',') WITHIN GROUP (ORDER BY ic.key_ordinal) AS columns
              FROM sys.indexes i
              JOIN sys.tables t ON t.object_id = i.object_id
              JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
              JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
              JOIN sys.schemas s ON s.schema_id = t.schema_id
              WHERE s.name = @schema AND i.is_primary_key = 0 AND i.name IS NOT NULL
              GROUP BY t.name, i.name, i.is_unique, i.type_desc
              ORDER BY t.name, i.name;",
            conn);
        cmd.Parameters.AddWithValue("@schema", schema);

        await using var reader = await cmd.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
        {
            var tableName = reader.GetString(0);
            var indexName = reader.GetString(1);
            var isUnique = reader.GetBoolean(2);
            var method = reader.GetString(3);
            var columns = reader.GetString(4).Split(',').ToList();

            if (!result.TryGetValue(tableName, out var list))
            {
                list = new List<IndexInfo>();
                result[tableName] = list;
            }

            list.Add(new IndexInfo(indexName, columns, isUnique, method));
        }

        return result;
    }

    private static async Task<Dictionary<string, List<TriggerInfo>>> GetTriggersAsync(SqlConnection conn, string schema, CancellationToken token)
    {
        // SQL Server triggers are always statement-level and are either AFTER or INSTEAD OF
        // (there is no row-level/FOR EACH ROW concept, unlike Postgres).
        var order = new List<(string Table, string Name)>();
        var byKey = new Dictionary<(string Table, string Name), (string Timing, bool IsDisabled, string Statement, List<string> Events)>();

        await using var cmd = new SqlCommand(
            @"SELECT t.name AS table_name, tr.name AS trigger_name, tr.is_instead_of_trigger, tr.is_disabled,
                     te.type_desc AS event_type, sm.definition
              FROM sys.triggers tr
              JOIN sys.tables t ON t.object_id = tr.parent_id
              JOIN sys.schemas s ON s.schema_id = t.schema_id
              JOIN sys.trigger_events te ON te.object_id = tr.object_id
              LEFT JOIN sys.sql_modules sm ON sm.object_id = tr.object_id
              WHERE s.name = @schema AND tr.is_ms_shipped = 0
              ORDER BY t.name, tr.name;",
            conn);
        cmd.Parameters.AddWithValue("@schema", schema);

        await using var reader = await cmd.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
        {
            var tableName = reader.GetString(0);
            var triggerName = reader.GetString(1);
            var isInsteadOf = reader.GetBoolean(2);
            var isDisabled = reader.GetBoolean(3);
            var eventType = reader.GetString(4);
            var statement = reader.IsDBNull(5) ? "" : reader.GetString(5);

            var key = (tableName, triggerName);
            if (!byKey.TryGetValue(key, out var entry))
            {
                entry = (isInsteadOf ? "INSTEAD OF" : "AFTER", isDisabled, statement, new List<string>());
                byKey[key] = entry;
                order.Add(key);
            }

            entry.Events.Add(eventType);
        }

        var result = new Dictionary<string, List<TriggerInfo>>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in order)
        {
            var entry = byKey[key];

            if (!result.TryGetValue(key.Table, out var list))
            {
                list = new List<TriggerInfo>();
                result[key.Table] = list;
            }

            list.Add(new TriggerInfo(key.Name, entry.Timing, "STATEMENT", entry.Events, entry.Statement, !entry.IsDisabled));
        }

        return result;
    }

    private static async Task<Dictionary<string, string>> GetTableCommentsAsync(SqlConnection conn, string schema, CancellationToken token)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        await using var cmd = new SqlCommand(
            @"SELECT t.name AS table_name, CAST(ep.value AS NVARCHAR(MAX))
              FROM sys.objects t
              JOIN sys.schemas s ON s.schema_id = t.schema_id
              JOIN sys.extended_properties ep ON ep.major_id = t.object_id AND ep.minor_id = 0 AND ep.name = 'MS_Description'
              WHERE s.name = @schema AND t.type IN ('U', 'V');",
            conn);
        cmd.Parameters.AddWithValue("@schema", schema);

        await using var reader = await cmd.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
        {
            if (reader.IsDBNull(1)) continue;
            result[reader.GetString(0)] = reader.GetString(1);
        }

        return result;
    }

    private static async Task<Dictionary<(string Table, string Column), string>> GetColumnCommentsAsync(SqlConnection conn, string schema, CancellationToken token)
    {
        var result = new Dictionary<(string, string), string>();

        await using var cmd = new SqlCommand(
            @"SELECT t.name AS table_name, c.name AS column_name, CAST(ep.value AS NVARCHAR(MAX))
              FROM sys.columns c
              JOIN sys.objects t ON t.object_id = c.object_id AND t.type IN ('U', 'V')
              JOIN sys.schemas s ON s.schema_id = t.schema_id
              JOIN sys.extended_properties ep ON ep.major_id = t.object_id AND ep.minor_id = c.column_id AND ep.name = 'MS_Description'
              WHERE s.name = @schema;",
            conn);
        cmd.Parameters.AddWithValue("@schema", schema);

        await using var reader = await cmd.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
        {
            if (reader.IsDBNull(2)) continue;
            result[(reader.GetString(0), reader.GetString(1))] = reader.GetString(2);
        }

        return result;
    }
}
