using System.Text;
using System.Text.RegularExpressions;
using SchemaFlex.Core.Models;

namespace SchemaFlex.Core;

/// <summary>
/// Narrows a schema's table list to what the caller wants shown, by name, with
/// glob-style (<c>*</c>) or SQL LIKE-style (<c>%</c>) wildcards. Foreign keys are
/// left untouched even when they point at an excluded table - the ERD viewer
/// already tolerates a dangling reference (it just skips drawing that edge),
/// and the column still deserves its FK marker and "FK -> table" tag.
/// </summary>
public static class TableFilter
{
    public static List<Table> Apply(List<Table> tables, IReadOnlyList<string>? includePatterns, IReadOnlyList<string>? excludePatterns)
    {
        IEnumerable<Table> result = tables;

        if (includePatterns is { Count: > 0 })
        {
            var regexes = includePatterns.Select(ToRegex).ToList();
            result = result.Where(t => regexes.Any(r => r.IsMatch(t.Name)));
        }

        if (excludePatterns is { Count: > 0 })
        {
            var regexes = excludePatterns.Select(ToRegex).ToList();
            result = result.Where(t => !regexes.Any(r => r.IsMatch(t.Name)));
        }

        return result.ToList();
    }

    private static Regex ToRegex(string pattern)
    {
        var sb = new StringBuilder("^");
        foreach (var c in pattern)
        {
            sb.Append(c is '*' or '%' ? ".*" : Regex.Escape(c.ToString()));
        }
        sb.Append('$');
        return new Regex(sb.ToString(), RegexOptions.IgnoreCase);
    }
}
