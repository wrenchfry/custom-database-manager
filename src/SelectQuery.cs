using System.Globalization;
using System.Text.RegularExpressions;

namespace CodecraftersSqlite;

public sealed record SelectQuery(IReadOnlyList<string> Columns, string Table, WhereFilter? Filter)
{
    public bool IsCount => Columns is ["count(*)"];
    public static SelectQuery Parse(string input)
    {
        var match = Regex.Match(input, "^\\s*select\\s+(?<columns>.+?)\\s+from\\s+(?<table>[^\\s]+)(?:\\s+where\\s+(?<where>.+?))?\\s*;?\\s*$", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (!match.Success) throw new ArgumentException("Only SELECT <columns> FROM <table> [WHERE <column> = <value>] is supported.");
        var columns = match.Groups["columns"].Value.Split(',').Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
        return new SelectQuery(columns, SqliteDatabase.Unquote(match.Groups["table"].Value), match.Groups["where"].Success ? WhereFilter.Parse(match.Groups["where"].Value) : null);
    }
}

public sealed record WhereFilter(string Column, object? Expected)
{
    public static WhereFilter Parse(string input)
    {
        var match = Regex.Match(input, "^\\s*(?<column>[^\\s=]+)\\s*=\\s*(?<value>.*?)\\s*$", RegexOptions.Singleline);
        if (!match.Success) throw new ArgumentException("WHERE currently supports equality comparisons only.");
        var value = match.Groups["value"].Value.Trim();
        object? expected = value.Equals("null", StringComparison.OrdinalIgnoreCase) ? null
            : value.Length >= 2 && value[0] == '\'' && value[^1] == '\'' ? value[1..^1].Replace("''", "'")
            : long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer) ? integer
            : double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ? number
            : throw new ArgumentException("WHERE values must be quoted text, NULL, or a number.");
        return new WhereFilter(SqliteDatabase.Unquote(match.Groups["column"].Value), expected);
    }

    public bool Matches(SqliteRow row, IReadOnlyList<string> columns)
    {
        var actual = row.Raw(Column, columns);
        return (actual, Expected) switch
        {
            (null, null) => true, (long left, long right) => left == right,
            (double left, double right) => left.Equals(right), (long left, double right) => left.Equals(right),
            (double left, long right) => left.Equals(right),
            _ => string.Equals(SqliteRow.Format(actual), SqliteRow.Format(Expected), StringComparison.Ordinal)
        };
    }
}
