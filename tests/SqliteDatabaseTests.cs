using CodecraftersSqlite;
using Xunit;

namespace CodecraftersSqlite.Tests;

public sealed class SqliteDatabaseTests
{
    [Fact]
    public void SampleDatabaseExposesTablesAndRows()
    {
        using var database = new SqliteDatabase(Path.Combine(AppContext.BaseDirectory, "sample.db"));
        var table = database.GetTable("apples");
        var rows = database.ReadRows(table).ToList();

        Assert.True(database.PageSize >= 512);
        Assert.Contains("apples", database.Tables.Select(item => item.Name));
        Assert.NotEmpty(rows);
        Assert.Contains("name", table.Columns);
    }

    [Fact]
    public void ConstructorRejectsNonSqliteFiles()
    {
        var path = Path.GetTempFileName();
        File.WriteAllText(path, "not a SQLite database");

        try
        {
            Assert.Throws<InvalidDataException>(() => new SqliteDatabase(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void OverviewAndQueryExposeDatabaseData()
    {
        using var database = OpenSampleDatabase();

        var overview = database.Overview();
        var result = database.RunQuery("SELECT id, name FROM apples WHERE id = 1");

        Assert.Equal("UTF-8", overview.TextEncoding);
        Assert.True(overview.PageCount > 0);
        Assert.Equal(["id", "name"], result.Columns);
        Assert.Equal("1", result.Rows.Single()[0]);
        Assert.Equal("Granny Smith", result.Rows.Single()[1]);
        Assert.False(result.Truncated);
    }

    [Fact]
    public void PageInspectionAndTreeDescribeTablePages()
    {
        using var database = OpenSampleDatabase();
        var table = database.GetTable("apples");

        var page = database.InspectPage(table.RootPage);
        var tree = database.GetTableTree(table.Name);

        Assert.Equal(table.RootPage, page.PageNumber);
        Assert.Contains("table B-tree", page.PageType, StringComparison.OrdinalIgnoreCase);
        Assert.NotEmpty(page.HexPreview);
        Assert.Contains(tree, node => node.PageNumber == table.RootPage);
    }

    private static SqliteDatabase OpenSampleDatabase() => new(Path.Combine(AppContext.BaseDirectory, "sample.db"));
}
