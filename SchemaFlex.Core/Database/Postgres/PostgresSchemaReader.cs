using Npgsql;
using SchemaFlex.Core.Models;

namespace SchemaFlex.Core.Database.Postgres;

public class PostgresSchemaReader : ISchemaReader
{
    public async Task<SchemaData> ReadSchemaAsync(string connectionString, string schema, CancellationToken token)
    {
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        await using var conn = await dataSource.OpenConnectionAsync(token);

        var tableEntries = await GetTableNamesAsync(conn, schema, token);
        var tableNames = tableEntries.Select(t => t.Name).ToList();
        var viewNames = new HashSet<string>(tableEntries.Where(t => t.IsView).Select(t => t.Name), StringComparer.OrdinalIgnoreCase);

        var primaryKeysByTable = await GetPrimaryKeysAsync(conn, schema, token);
        var uniqueColumnsByTable = await GetUniqueColumnsAsync(conn, schema, token);
        var foreignKeysByTable = await GetForeignKeysAsync(conn, schema, token);
        var checkConstraintsByTable = await GetCheckConstraintsAsync(conn, schema, token);
        var indexesByTable = await GetIndexesAsync(conn, schema, token);
        var triggersByTable = await GetTriggersAsync(conn, schema, token);
        var tableCommentsByTable = await GetTableCommentsAsync(conn, schema, token);
        var columnCommentsByTable = await GetColumnCommentsAsync(conn, schema, token);
        var columnsByTable = await GetColumnsAsync(conn, schema, primaryKeysByTable, uniqueColumnsByTable, columnCommentsByTable, token);
        var enums = await GetEnumsAsync(conn, schema, token);

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

        return new SchemaData(schema, DateTimeOffset.UtcNow, tables, enums, DatabaseProvider.PostgreSql);
    }

    private static async Task<List<(string Name, bool IsView)>> GetTableNamesAsync(NpgsqlConnection conn, string schema, CancellationToken token)
    {
        var tableNames = new List<(string Name, bool IsView)>();

        await using var cmd = new NpgsqlCommand(
            @"SELECT table_name, table_type FROM information_schema.tables
              WHERE table_schema = @schema AND table_type IN ('BASE TABLE', 'VIEW')
              ORDER BY table_name;",
            conn);
        cmd.Parameters.AddWithValue("schema", schema);

        await using var reader = await cmd.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
        {
            var isView = string.Equals(reader.GetString(1), "VIEW", StringComparison.OrdinalIgnoreCase);
            tableNames.Add((reader.GetString(0), isView));
        }

        return tableNames;
    }

    private static async Task<Dictionary<string, List<Column>>> GetColumnsAsync(
        NpgsqlConnection conn,
        string schema,
        Dictionary<string, HashSet<string>> primaryKeysByTable,
        Dictionary<string, HashSet<string>> uniqueColumnsByTable,
        Dictionary<(string Table, string Column), string> columnCommentsByTable,
        CancellationToken token)
    {
        var result = new Dictionary<string, List<Column>>(StringComparer.OrdinalIgnoreCase);

        // pg_catalog.format_type() renders the column's full declared type - including
        // length/precision/scale (e.g. "character varying(255)", "numeric(10,2)") and,
        // for enums and other named types, just the type name itself - which is exactly
        // what lets the viewer match a column up against the enum definitions fetched
        // separately, without a separate USER-DEFINED/udt_name special case. Identity
        // and generated columns are distinct features (GENERATED ... AS IDENTITY vs
        // GENERATED ... AS (expr) STORED) that both affect whether a caller can, or
        // must, supply a value - worth surfacing separately from a plain default.
        await using var cmd = new NpgsqlCommand(
            @"SELECT c.table_name, c.column_name, pg_catalog.format_type(a.atttypid, a.atttypmod) AS data_type,
                     c.is_nullable, c.column_default, c.is_identity, c.identity_generation,
                     c.is_generated, c.generation_expression
              FROM information_schema.columns c
              JOIN pg_catalog.pg_class pc ON pc.relname = c.table_name
              JOIN pg_catalog.pg_namespace pn ON pn.oid = pc.relnamespace AND pn.nspname = c.table_schema
              JOIN pg_catalog.pg_attribute a ON a.attrelid = pc.oid AND a.attname = c.column_name
                   AND a.attnum > 0 AND NOT a.attisdropped
              WHERE c.table_schema = @schema
              ORDER BY c.table_name, c.ordinal_position;",
            conn);
        cmd.Parameters.AddWithValue("schema", schema);

        await using var reader = await cmd.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
        {
            var tableName = reader.GetString(0);
            var columnName = reader.GetString(1);
            var dataType = reader.IsDBNull(2) ? "unknown" : reader.GetString(2);
            var isNullable = !reader.IsDBNull(3) && string.Equals(reader.GetString(3), "YES", StringComparison.OrdinalIgnoreCase);
            var columnDefault = reader.IsDBNull(4) ? null : reader.GetString(4);
            var isIdentity = !reader.IsDBNull(5) && string.Equals(reader.GetString(5), "YES", StringComparison.OrdinalIgnoreCase);
            var isGenerated = !reader.IsDBNull(7) && string.Equals(reader.GetString(7), "ALWAYS", StringComparison.OrdinalIgnoreCase);
            var generationExpression = isGenerated && !reader.IsDBNull(8) ? reader.GetString(8) : null;
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

    private static async Task<Dictionary<string, HashSet<string>>> GetUniqueColumnsAsync(NpgsqlConnection conn, string schema, CancellationToken token)
    {
        var result = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        await using var cmd = new NpgsqlCommand(
            @"SELECT tc.table_name, kcu.column_name
              FROM information_schema.table_constraints tc
              JOIN information_schema.key_column_usage kcu
                ON tc.constraint_name = kcu.constraint_name AND tc.table_schema = kcu.table_schema
              WHERE tc.constraint_type = 'UNIQUE' AND tc.table_schema = @schema;",
            conn);
        cmd.Parameters.AddWithValue("schema", schema);

        await using var reader = await cmd.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
        {
            var tableName = reader.GetString(0);
            var columnName = reader.GetString(1);

            if (!result.TryGetValue(tableName, out var set))
            {
                set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                result[tableName] = set;
            }

            set.Add(columnName);
        }

        return result;
    }

    private static async Task<Dictionary<string, List<CheckConstraint>>> GetCheckConstraintsAsync(NpgsqlConnection conn, string schema, CancellationToken token)
    {
        var result = new Dictionary<string, List<CheckConstraint>>(StringComparer.OrdinalIgnoreCase);

        // NOT NULL is enforced internally as a check constraint in Postgres and would
        // otherwise show up here as a redundant "column IS NOT NULL" entry - excluded
        // since is_nullable already covers it.
        await using var cmd = new NpgsqlCommand(
            @"SELECT c.relname AS table_name, con.conname, pg_catalog.pg_get_constraintdef(con.oid) AS definition
              FROM pg_catalog.pg_constraint con
              JOIN pg_catalog.pg_class c ON c.oid = con.conrelid
              JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
              WHERE con.contype = 'c' AND n.nspname = @schema AND NOT con.conislocal = false;",
            conn);
        cmd.Parameters.AddWithValue("schema", schema);

        await using var reader = await cmd.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
        {
            var tableName = reader.GetString(0);
            var name = reader.GetString(1);
            var definition = reader.IsDBNull(2) ? null : reader.GetString(2);
            if (definition == null || definition.Contains("IS NOT NULL", StringComparison.OrdinalIgnoreCase)) continue;

            if (!result.TryGetValue(tableName, out var list))
            {
                list = new List<CheckConstraint>();
                result[tableName] = list;
            }

            list.Add(new CheckConstraint(name, definition));
        }

        return result;
    }

    private static async Task<Dictionary<string, string>> GetTableCommentsAsync(NpgsqlConnection conn, string schema, CancellationToken token)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        await using var cmd = new NpgsqlCommand(
            @"SELECT c.relname AS table_name, d.description
              FROM pg_catalog.pg_class c
              JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
              JOIN pg_catalog.pg_description d ON d.objoid = c.oid AND d.objsubid = 0
              WHERE n.nspname = @schema AND c.relkind IN ('r', 'v');",
            conn);
        cmd.Parameters.AddWithValue("schema", schema);

        await using var reader = await cmd.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
        {
            if (reader.IsDBNull(1)) continue;
            result[reader.GetString(0)] = reader.GetString(1);
        }

        return result;
    }

    private static async Task<Dictionary<(string Table, string Column), string>> GetColumnCommentsAsync(NpgsqlConnection conn, string schema, CancellationToken token)
    {
        var result = new Dictionary<(string, string), string>();

        await using var cmd = new NpgsqlCommand(
            @"SELECT c.relname AS table_name, a.attname AS column_name, d.description
              FROM pg_catalog.pg_class c
              JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
              JOIN pg_catalog.pg_attribute a ON a.attrelid = c.oid AND a.attnum > 0 AND NOT a.attisdropped
              JOIN pg_catalog.pg_description d ON d.objoid = c.oid AND d.objsubid = a.attnum
              WHERE n.nspname = @schema AND c.relkind IN ('r', 'v');",
            conn);
        cmd.Parameters.AddWithValue("schema", schema);

        await using var reader = await cmd.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
        {
            if (reader.IsDBNull(2)) continue;
            result[(reader.GetString(0), reader.GetString(1))] = reader.GetString(2);
        }

        return result;
    }

    private static async Task<List<EnumType>> GetEnumsAsync(NpgsqlConnection conn, string schema, CancellationToken token)
    {
        var byName = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var order = new List<string>();

        await using var cmd = new NpgsqlCommand(
            @"SELECT t.typname, e.enumlabel
              FROM pg_catalog.pg_type t
              JOIN pg_catalog.pg_enum e ON t.oid = e.enumtypid
              JOIN pg_catalog.pg_namespace n ON n.oid = t.typnamespace
              WHERE n.nspname = @schema
              ORDER BY t.typname, e.enumsortorder;",
            conn);
        cmd.Parameters.AddWithValue("schema", schema);

        await using var reader = await cmd.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
        {
            var name = reader.GetString(0);
            var label = reader.GetString(1);

            if (!byName.TryGetValue(name, out var values))
            {
                values = new List<string>();
                byName[name] = values;
                order.Add(name);
            }

            values.Add(label);
        }

        return order.Select(name => new EnumType(name, byName[name])).ToList();
    }

    private static async Task<Dictionary<string, HashSet<string>>> GetPrimaryKeysAsync(NpgsqlConnection conn, string schema, CancellationToken token)
    {
        var result = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        await using var cmd = new NpgsqlCommand(
            @"SELECT tc.table_name, kcu.column_name
              FROM information_schema.table_constraints tc
              JOIN information_schema.key_column_usage kcu
                ON tc.constraint_name = kcu.constraint_name AND tc.table_schema = kcu.table_schema
              WHERE tc.constraint_type = 'PRIMARY KEY' AND tc.table_schema = @schema;",
            conn);
        cmd.Parameters.AddWithValue("schema", schema);

        await using var reader = await cmd.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
        {
            var tableName = reader.GetString(0);
            var columnName = reader.GetString(1);

            if (!result.TryGetValue(tableName, out var set))
            {
                set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                result[tableName] = set;
            }

            set.Add(columnName);
        }

        return result;
    }

    private static async Task<Dictionary<string, List<ForeignKey>>> GetForeignKeysAsync(NpgsqlConnection conn, string schema, CancellationToken token)
    {
        var result = new Dictionary<string, List<ForeignKey>>(StringComparer.OrdinalIgnoreCase);

        await using var cmd = new NpgsqlCommand(
            @"SELECT tc.constraint_name, tc.table_name, kcu.column_name, ccu.table_name AS foreign_table_name, ccu.column_name AS foreign_column_name,
                     rc.update_rule, rc.delete_rule
              FROM information_schema.table_constraints tc
              JOIN information_schema.key_column_usage kcu
                ON tc.constraint_name = kcu.constraint_name AND tc.table_schema = kcu.table_schema
              JOIN information_schema.constraint_column_usage ccu
                ON ccu.constraint_name = tc.constraint_name AND ccu.table_schema = tc.table_schema
              JOIN information_schema.referential_constraints rc
                ON rc.constraint_name = tc.constraint_name AND rc.constraint_schema = tc.table_schema
              WHERE tc.constraint_type = 'FOREIGN KEY' AND tc.table_schema = @schema;",
            conn);
        cmd.Parameters.AddWithValue("schema", schema);

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

    private static async Task<Dictionary<string, List<IndexInfo>>> GetIndexesAsync(NpgsqlConnection conn, string schema, CancellationToken token)
    {
        var result = new Dictionary<string, List<IndexInfo>>(StringComparer.OrdinalIgnoreCase);

        // Primary-key indexes are excluded - the PK badge on each column already
        // says that. Everything else (explicit UNIQUE indexes, plain lookup
        // indexes, partial/expression indexes) shows up so a reader can tell what's
        // actually indexed for lookups, not just what's constrained.
        await using var cmd = new NpgsqlCommand(
            @"SELECT
                  t.relname AS table_name,
                  i.relname AS index_name,
                  ix.indisunique AS is_unique,
                  am.amname AS method,
                  array_agg(a.attname ORDER BY array_position(ix.indkey, a.attnum)) AS columns
              FROM pg_catalog.pg_index ix
              JOIN pg_catalog.pg_class t ON t.oid = ix.indrelid
              JOIN pg_catalog.pg_class i ON i.oid = ix.indexrelid
              JOIN pg_catalog.pg_namespace n ON n.oid = t.relnamespace
              JOIN pg_catalog.pg_am am ON am.oid = i.relam
              JOIN pg_catalog.pg_attribute a ON a.attrelid = t.oid AND a.attnum = ANY(ix.indkey)
              WHERE n.nspname = @schema AND t.relkind = 'r' AND NOT ix.indisprimary
              GROUP BY t.relname, i.relname, ix.indisunique, am.amname
              ORDER BY t.relname, i.relname;",
            conn);
        cmd.Parameters.AddWithValue("schema", schema);

        await using var reader = await cmd.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
        {
            var tableName = reader.GetString(0);
            var indexName = reader.GetString(1);
            var isUnique = reader.GetBoolean(2);
            var method = reader.GetString(3);
            var columns = reader.GetFieldValue<string[]>(4).ToList();

            if (!result.TryGetValue(tableName, out var list))
            {
                list = new List<IndexInfo>();
                result[tableName] = list;
            }

            list.Add(new IndexInfo(indexName, columns, isUnique, method));
        }

        return result;
    }

    private static async Task<Dictionary<string, List<TriggerInfo>>> GetTriggersAsync(NpgsqlConnection conn, string schema, CancellationToken token)
    {
        // A trigger fired on multiple events (e.g. "INSERT OR UPDATE") shows up as
        // one row per event in information_schema.triggers, all sharing the same
        // name/timing/statement - grouped back into one entry with an events list.
        var order = new List<(string Table, string Name)>();
        var byKey = new Dictionary<(string Table, string Name), (string Timing, string Level, string Statement, List<string> Events)>();

        await using (var cmd = new NpgsqlCommand(
            @"SELECT event_object_table, trigger_name, action_timing, action_orientation, event_manipulation, action_statement
              FROM information_schema.triggers
              WHERE trigger_schema = @schema
              ORDER BY event_object_table, trigger_name, event_manipulation;",
            conn))
        {
            cmd.Parameters.AddWithValue("schema", schema);

            await using var reader = await cmd.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
            {
                var tableName = reader.GetString(0);
                var triggerName = reader.GetString(1);
                var timing = reader.IsDBNull(2) ? "" : reader.GetString(2);
                var level = reader.IsDBNull(3) ? "" : reader.GetString(3);
                var eventManipulation = reader.IsDBNull(4) ? "" : reader.GetString(4);
                var statement = reader.IsDBNull(5) ? "" : reader.GetString(5);

                var key = (tableName, triggerName);
                if (!byKey.TryGetValue(key, out var entry))
                {
                    entry = (timing, level, statement, new List<string>());
                    byKey[key] = entry;
                    order.Add(key);
                }

                entry.Events.Add(eventManipulation);
            }
        }

        var enabledByKey = new Dictionary<(string Table, string Name), bool>();
        await using (var cmd = new NpgsqlCommand(
            @"SELECT c.relname AS table_name, t.tgname, t.tgenabled::text
              FROM pg_catalog.pg_trigger t
              JOIN pg_catalog.pg_class c ON c.oid = t.tgrelid
              JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
              WHERE n.nspname = @schema AND NOT t.tgisinternal;",
            conn))
        {
            cmd.Parameters.AddWithValue("schema", schema);

            await using var reader = await cmd.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
            {
                var tableName = reader.GetString(0);
                var triggerName = reader.GetString(1);
                // tgenabled is Postgres' internal single-byte "char" type, which Npgsql
                // refuses to read via GetString unless cast to text in the query above.
                var tgenabled = reader.IsDBNull(2) ? "O" : reader.GetString(2);

                enabledByKey[(tableName, triggerName)] = tgenabled != "D";
            }
        }

        var result = new Dictionary<string, List<TriggerInfo>>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in order)
        {
            var entry = byKey[key];
            var isEnabled = !enabledByKey.TryGetValue(key, out var enabled) || enabled;

            if (!result.TryGetValue(key.Table, out var list))
            {
                list = new List<TriggerInfo>();
                result[key.Table] = list;
            }

            list.Add(new TriggerInfo(key.Name, entry.Timing, entry.Level, entry.Events, entry.Statement, isEnabled));
        }

        return result;
    }
}
