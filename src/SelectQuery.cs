using System.Globalization;
using System.Text.RegularExpressions;

namespace SqliteUnderTheHood;

/// <summary>
/// Represents the deliberately small SELECT grammar supported by the custom reader.
/// </summary>
public sealed record SelectQuery(IReadOnlyList<string> Columns, string Table, WhereFilter? Filter)
{
    public bool IsCount => Columns is ["count(*)"];

    public static SelectQuery Parse(string input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (input.Length > 4_096)
        {
            throw new ArgumentException("Queries must be 4,096 characters or shorter.", nameof(input));
        }

        var match = Regex.Match(
            input,
            "^\\s*select\\s+(?<columns>.+?)\\s+from\\s+(?<table>[A-Za-z_][A-Za-z0-9_]*)\\s*(?:where\\s+(?<where>.+?))?\\s*;?\\s*$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline,
            TimeSpan.FromMilliseconds(100));
        if (!match.Success)
        {
            throw new ArgumentException("Only SELECT <columns> FROM <table> [WHERE <column> = <value>] is supported.");
        }

        var columns = match.Groups["columns"].Value.Split(',').Select(x => x.Trim())
            .Select(column => column.Equals("count(*)", StringComparison.OrdinalIgnoreCase) ? "count(*)" : column)
            .ToList();
        if (columns.Any(column => !IsColumnName(column))
            && !(columns.Count == 1 && columns[0].Equals("count(*)", StringComparison.OrdinalIgnoreCase)))
        {
            if (columns is not ["*"])
            {
                throw new ArgumentException("Select column names must be simple identifiers, *, or count(*).");
            }
        }

        if (columns.Contains("*", StringComparer.Ordinal) && columns.Count != 1
            || columns.Any(column => column.Equals("count(*)", StringComparison.OrdinalIgnoreCase)) && columns.Count != 1)
        {
            throw new ArgumentException("Use * or count(*) by itself in the SELECT list.");
        }

        var filter = match.Groups["where"].Success ? WhereFilter.Parse(match.Groups["where"].Value) : null;
        return new SelectQuery(columns, match.Groups["table"].Value, filter);
    }

    private static bool IsColumnName(string value) =>
        Regex.IsMatch(value, "^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
}

public sealed record WhereFilter(string Column, object? Expected)
{
    public static WhereFilter Parse(string input)
    {
        var match = Regex.Match(
            input,
            "^\\s*(?<column>[A-Za-z_][A-Za-z0-9_]*)\\s*=\\s*(?<value>.*?)\\s*$",
            RegexOptions.CultureInvariant | RegexOptions.Singleline,
            TimeSpan.FromMilliseconds(100));
        if (!match.Success) throw new ArgumentException("WHERE currently supports simple column equality comparisons only.");
        var value = match.Groups["value"].Value.Trim();
        object? expected = value.Equals("null", StringComparison.OrdinalIgnoreCase) ? null
            : value.Length >= 2 && value[0] == '\'' && value[^1] == '\'' ? value[1..^1].Replace("''", "'")
            : long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer) ? (object)integer
            : double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ? number
            : throw new ArgumentException("WHERE values must be quoted text, NULL, or a number.");
        return new WhereFilter(SqliteDatabase.Unquote(match.Groups["column"].Value), expected);
    }

    public bool Matches(SqliteRow row, IReadOnlyList<string> columns)
    {
        var actual = row.Raw(Column, columns);
        return (actual, Expected) switch
        {
            (null, null) => false,
            (long left, long right) => left == right,
            (double left, double right) => left.Equals(right),
            (long left, double right) => left.Equals(right),
            (double left, long right) => left.Equals(right),
            _ => string.Equals(SqliteRow.Format(actual), SqliteRow.Format(Expected), StringComparison.Ordinal)
        };
    }
}
