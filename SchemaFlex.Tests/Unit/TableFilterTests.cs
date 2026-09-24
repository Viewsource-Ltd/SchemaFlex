using SchemaFlex.Core;
using SchemaFlex.Core.Models;

namespace SchemaFlex.Tests.Unit;

public class TableFilterTests
{
    private static Table MakeTable(string name) => new(
        name,
        new List<Column>(),
        new List<ForeignKey>(),
        new List<CheckConstraint>(),
        new List<IndexInfo>(),
        new List<TriggerInfo>(),
        null);

    private static List<Table> SampleTables() =>
        new() { MakeTable("users"), MakeTable("orders"), MakeTable("orders_audit"), MakeTable("sessions") };

    [Fact]
    public void No_patterns_keeps_everything()
    {
        var result = TableFilter.Apply(SampleTables(), null, null);

        Assert.Equal(new[] { "users", "orders", "orders_audit", "sessions" }, result.Select(t => t.Name));
    }

    [Fact]
    public void Include_pattern_with_asterisk_keeps_only_matches()
    {
        var result = TableFilter.Apply(SampleTables(), new[] { "order*" }, null);

        Assert.Equal(new[] { "orders", "orders_audit" }, result.Select(t => t.Name));
    }

    [Fact]
    public void Include_pattern_with_percent_wildcard_behaves_like_asterisk()
    {
        var result = TableFilter.Apply(SampleTables(), new[] { "order%" }, null);

        Assert.Equal(new[] { "orders", "orders_audit" }, result.Select(t => t.Name));
    }

    [Fact]
    public void Exclude_pattern_removes_matches()
    {
        var result = TableFilter.Apply(SampleTables(), null, new[] { "*_audit" });

        Assert.Equal(new[] { "users", "orders", "sessions" }, result.Select(t => t.Name));
    }

    [Fact]
    public void Exclude_is_applied_after_include()
    {
        var result = TableFilter.Apply(SampleTables(), new[] { "order*" }, new[] { "*_audit" });

        Assert.Equal(new[] { "orders" }, result.Select(t => t.Name));
    }

    [Fact]
    public void Matching_is_case_insensitive()
    {
        var result = TableFilter.Apply(SampleTables(), new[] { "USERS" }, null);

        Assert.Equal(new[] { "users" }, result.Select(t => t.Name));
    }

    [Fact]
    public void Exact_pattern_without_wildcard_matches_only_that_name()
    {
        var result = TableFilter.Apply(SampleTables(), new[] { "orders" }, null);

        Assert.Equal(new[] { "orders" }, result.Select(t => t.Name));
    }
}
