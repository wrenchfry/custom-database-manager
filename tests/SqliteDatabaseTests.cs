using System.Buffers.Binary;
using System.Globalization;
using Microsoft.Data.Sqlite;
using SqliteUnderTheHood;
using Xunit;

namespace SqliteUnderTheHood.Tests;

public sealed class SqliteDatabaseTests
{
    [Theory]
    [InlineData(512)]
    [InlineData(1024)]
    [InlineData(4096)]
    public void ReadsValuesFromRealSqliteDatabasesWithDifferentPageSizes(int pageSize)
    {
        var path = CreateDatabase(pageSize);
        try
        {
            using var database = new SqliteDatabase(path);
            using var connection = new SqliteConnection($"Data Source={path};Mode=ReadOnly;Pooling=False");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT id, name, ratio, payload, note FROM apples WHERE id = 1";
            using var reader = command.ExecuteReader();
            Assert.True(reader.Read());
            var expected = Enumerable.Range(0, reader.FieldCount).Select(index => Format(reader.GetValue(index))).ToArray();
            connection.Close();

            var result = database.RunQuery("SELECT id, name, ratio, payload, note FROM apples WHERE id = 1");

            Assert.Equal(expected, result.Rows.Single());
            Assert.Equal("UTF-8", database.TextEncoding);
            Assert.Contains("apples", database.Tables.Select(table => table.Name));
            Assert.Equal(pageSize, database.PageSize);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void DecodesUtf16TextUsingTheDatabaseEncoding()
    {
        var path = CreateDatabase(encoding: "UTF-16le");
        try
        {
            using var database = new SqliteDatabase(path);

            Assert.Equal("UTF-16 little-endian", database.TextEncoding);
            Assert.Equal("Granny Smith", database.RunQuery("SELECT name FROM apples WHERE id = 1").Rows.Single().Single());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ReadsEmptyTablesNullValuesAndOverflowPayloads()
    {
        var path = CreateDatabase();
        try
        {
            using var database = new SqliteDatabase(path);

            Assert.Empty(database.RunQuery("SELECT * FROM empty_rows").Rows);
            Assert.Equal("NULL", database.RunQuery("SELECT note FROM apples WHERE id = 1").Rows.Single().Single());
            Assert.Empty(database.RunQuery("SELECT * FROM apples WHERE note = NULL").Rows);

            var largeText = database.RunQuery("SELECT name FROM apples WHERE id = 2").Rows.Single().Single();
            Assert.Equal(12_000, largeText.Length);

            var tree = database.GetTableTree("apples");
            Assert.True(tree.Count > 1);
            Assert.Contains(tree.Where(node => node.PageType == "Leaf table B-tree")
                .SelectMany(node => database.InspectPage(node.PageNumber).Cells), cell => cell.OverflowPages.Count > 0);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void RejectsTruncatedFilesAndInvalidCellPointersAsDatabaseErrors()
    {
        var databasePath = CreateDatabase();
        var truncatedPath = Path.GetTempFileName();
        var invalidPointerPath = Path.GetTempFileName();
        try
        {
            var bytes = File.ReadAllBytes(databasePath);
            File.WriteAllBytes(truncatedPath, bytes[..^1]);
            Assert.Throws<InvalidDataException>(() => new SqliteDatabase(truncatedPath));

            BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(108, sizeof(ushort)), ushort.MaxValue);
            File.WriteAllBytes(invalidPointerPath, bytes);
            Assert.Throws<InvalidDataException>(() => new SqliteDatabase(invalidPointerPath));
        }
        finally
        {
            File.Delete(databasePath);
            File.Delete(truncatedPath);
            File.Delete(invalidPointerPath);
        }
    }

    [Fact]
    public void RejectsOutOfRangeAndCyclicBtreePageReferences()
    {
        var databasePath = CreateDatabase();
        var invalidPath = Path.GetTempFileName();
        var cyclicPath = Path.GetTempFileName();
        try
        {
            using var database = new SqliteDatabase(databasePath);
            var rootPage = database.GetTable("apples").RootPage;
            var rootOffset = (rootPage - 1) * database.PageSize;
            var headerOffset = rootOffset + (rootPage == 1 ? 100 : 0);
            Assert.Equal(0x05, File.ReadAllBytes(databasePath)[headerOffset]);

            var invalidReference = File.ReadAllBytes(databasePath);
            var cellOffset = BinaryPrimitives.ReadUInt16BigEndian(invalidReference.AsSpan(headerOffset + 12, sizeof(ushort)));
            BinaryPrimitives.WriteUInt32BigEndian(
                invalidReference.AsSpan(rootOffset + cellOffset, sizeof(uint)),
                checked((uint)database.PageCount + 1));
            File.WriteAllBytes(invalidPath, invalidReference);
            using (var malformed = new SqliteDatabase(invalidPath))
            {
                Assert.Throws<InvalidDataException>(() => malformed.GetTableTree("apples"));
            }

            var cyclicReference = File.ReadAllBytes(databasePath);
            BinaryPrimitives.WriteUInt32BigEndian(
                cyclicReference.AsSpan(rootOffset + cellOffset, sizeof(uint)),
                checked((uint)rootPage));
            File.WriteAllBytes(cyclicPath, cyclicReference);
            using (var malformed = new SqliteDatabase(cyclicPath))
            {
                Assert.Throws<InvalidDataException>(() => malformed.GetTableTree("apples"));
            }
        }
        finally
        {
            File.Delete(databasePath);
            File.Delete(invalidPath);
            File.Delete(cyclicPath);
        }
    }

    [Fact]
    public void RejectsUnknownColumnsEvenWhenTheTableIsEmpty()
    {
        var path = CreateDatabase();
        try
        {
            using var database = new SqliteDatabase(path);

            Assert.Throws<ArgumentException>(() => database.RunQuery("SELECT missing FROM empty_rows"));
            Assert.Throws<ArgumentException>(() => database.RunQuery("SELECT id FROM empty_rows WHERE missing = 1"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void UsesOnlyTheDeclaredIntegerPrimaryKeyAsTheRowIdAlias()
    {
        var path = CreateDatabase();
        try
        {
            using var database = new SqliteDatabase(path);

            Assert.Equal("-42", database.RunQuery("SELECT key FROM other_keys").Rows.Single().Single());
            Assert.Equal("NULL", database.RunQuery("SELECT id FROM plain_ids").Rows.Single().Single());
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static string CreateDatabase(int pageSize = 512, string? encoding = null)
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.sqlite");
        using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        connection.Open();
        using (var setup = connection.CreateCommand())
        {
            setup.CommandText = $"""
                PRAGMA page_size = {pageSize};
                {(encoding is null ? string.Empty : $"PRAGMA encoding = '{encoding}';")}
                CREATE TABLE apples (
                    id INTEGER PRIMARY KEY,
                    name TEXT NOT NULL,
                    ratio REAL,
                    payload BLOB,
                    note TEXT
                );
                CREATE TABLE empty_rows (id INTEGER PRIMARY KEY, label TEXT);
                CREATE TABLE other_keys (key INTEGER PRIMARY KEY, value TEXT);
                CREATE TABLE plain_ids (id INTEGER, value TEXT);
                """;
            setup.ExecuteNonQuery();
        }

        using (var setup = connection.CreateCommand())
        {
            setup.CommandText = "INSERT INTO other_keys (key, value) VALUES (-42, 'negative'); INSERT INTO plain_ids (id, value) VALUES (NULL, 'missing');";
            setup.ExecuteNonQuery();
        }

        using var transaction = connection.BeginTransaction();
        using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = "INSERT INTO apples (id, name, ratio, payload, note) VALUES ($id, $name, $ratio, $payload, $note)";
        var id = insert.Parameters.Add("$id", SqliteType.Integer);
        var name = insert.Parameters.Add("$name", SqliteType.Text);
        var ratio = insert.Parameters.Add("$ratio", SqliteType.Real);
        var payload = insert.Parameters.Add("$payload", SqliteType.Blob);
        var note = insert.Parameters.Add("$note", SqliteType.Text);

        for (var index = 1; index <= 1_202; index++)
        {
            id.Value = index;
            name.Value = index switch
            {
                1 => "Granny Smith",
                2 => new string('x', 12_000),
                _ => $"apple-{index.ToString(CultureInfo.InvariantCulture)}"
            };
            ratio.Value = index + 0.25d;
            payload.Value = index == 1 ? new byte[] { 0, 127, 255 } : new byte[] { 1, 2, 3 };
            note.Value = index == 1 ? DBNull.Value : $"note-{index.ToString(CultureInfo.InvariantCulture)}";
            insert.ExecuteNonQuery();
        }

        transaction.Commit();
        return path;
    }

    private static string Format(object value) => value switch
    {
        DBNull => "NULL",
        byte[] blob => $"X'{Convert.ToHexString(blob)}'",
        double number => number.ToString(CultureInfo.InvariantCulture),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty
    };
}
