using SqliteUnderTheHood;
using Xunit;

namespace SqliteUnderTheHood.Tests;

public sealed class SelectQueryTests
{
    [Fact]
    public void ParseRecognizesSelectedColumnsAndFilter()
    {
        var query = SelectQuery.Parse("SELECT id, name FROM apples WHERE id = 1");

        Assert.Equal(["id", "name"], query.Columns);
        Assert.Equal("apples", query.Table);
        Assert.NotNull(query.Filter);
        Assert.Equal("id", query.Filter.Column);
        Assert.Equal(1L, Assert.IsType<long>(query.Filter.Expected));
    }

    [Theory]
    [InlineData("DELETE FROM apples")]
    [InlineData("SELECT name apples")]
    [InlineData("SELECT id, * FROM apples")]
    [InlineData("SELECT id FROM apples WHERE id = 1; DELETE FROM apples")]
    public void ParseRejectsUnsupportedStatements(string query)
    {
        Assert.Throws<ArgumentException>(() => SelectQuery.Parse(query));
    }

    [Fact]
    public void FilterMatchesTextAndNumericValues()
    {
        var row = new SqliteRow([1L, "Granny Smith"]);

        Assert.True(new WhereFilter("id", 1L).Matches(row, ["id", "name"]));
        Assert.True(new WhereFilter("name", "Granny Smith").Matches(row, ["id", "name"]));
        Assert.False(new WhereFilter("id", 2L).Matches(row, ["id", "name"]));
        Assert.False(new WhereFilter("name", null).Matches(new SqliteRow([1L, null]), ["id", "name"]));
    }
}
