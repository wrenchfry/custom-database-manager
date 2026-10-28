using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace CodecraftersSqlite;

/// <summary>
/// Reads the SQLite file format needed by the inspector without using a SQLite driver.
/// The implementation is intentionally read-only and supports a focused subset of page types.
/// </summary>
public sealed class SqliteDatabase : IDisposable
{
    private readonly byte[] bytes;
    private readonly List<TableDefinition> tables;
    public int PageSize { get; }
    public int PageCount => bytes.Length / PageSize;
    public long FileSizeBytes => bytes.LongLength;
    public string TextEncoding { get; }
    public IReadOnlyList<TableDefinition> Tables => tables;

    public SqliteDatabase(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        bytes = new byte[stream.Length];
        stream.ReadExactly(bytes);
        if (bytes.Length < 100 || Encoding.ASCII.GetString(bytes, 0, 16) != "SQLite format 3\0")
            throw new InvalidDataException("The file is not a SQLite 3 database.");
        PageSize = Read16(16) is 1 ? 65536 : Read16(16);
        if (PageSize < 512 || PageSize > 65536 || PageSize % 2 != 0) throw new InvalidDataException("Invalid page size.");
        if (bytes.Length % PageSize != 0) throw new InvalidDataException("The database file does not contain complete pages.");
        TextEncoding = Read32(56) switch
        {
            1 => "UTF-8",
            2 => "UTF-16 little-endian",
            3 => "UTF-16 big-endian",
            _ => "Unknown"
        };
        tables = ReadPage(1)
            .Select(row => new Schema(row.Text(0), row.Text(1), int.TryParse(row.Text(3), out var root) ? root : 0, row.Text(4)))
            .Where(x => x.Type.Equals("table", StringComparison.OrdinalIgnoreCase) && !x.Name.StartsWith("sqlite_", StringComparison.OrdinalIgnoreCase))
            .Select(x => new TableDefinition(x.Name, x.RootPage, x.Sql, Columns(x.Sql))).ToList();
    }

    public TableDefinition GetTable(string name) => tables.FirstOrDefault(x => x.Name.Equals(Unquote(name), StringComparison.OrdinalIgnoreCase))
        ?? throw new ArgumentException($"No table named '{name}' exists in this database.");
    public IEnumerable<SqliteRow> ReadRows(TableDefinition table) => ReadPage(table.RootPage);
    public DatabaseOverview Overview() => new(PageSize, PageCount, FileSizeBytes, TextEncoding, Tables);

    public QueryResult RunQuery(string queryText, int maximumRows = 500)
    {
        if (maximumRows is < 1 or > 1_000) throw new ArgumentOutOfRangeException(nameof(maximumRows));

        var query = SelectQuery.Parse(queryText);
        var table = GetTable(query.Table);
        var rows = ReadRows(table).Where(row => query.Filter is null || query.Filter.Matches(row, table.Columns));

        if (query.IsCount)
        {
            return new QueryResult(["count(*)"], [[rows.Count().ToString(CultureInfo.InvariantCulture)]], false);
        }

        var columns = query.Columns is ["*"] ? table.Columns : query.Columns;
        var materializedRows = rows.Take(maximumRows + 1).ToList();
        var truncated = materializedRows.Count > maximumRows;
        return new QueryResult(
            columns,
            materializedRows.Take(maximumRows).Select(row => (IReadOnlyList<string>)columns.Select(column => row.ValueFor(column, table.Columns)).ToList()).ToList(),
            truncated);
    }

    public PageInspection InspectPage(int pageNumber, int maximumPreviewBytes = 256)
    {
        if (maximumPreviewBytes is < 1 or > 1_024) throw new ArgumentOutOfRangeException(nameof(maximumPreviewBytes));

        var pageStart = PageStart(pageNumber);
        var headerOffset = pageStart + (pageNumber == 1 ? 100 : 0);
        var pageType = bytes[headerOffset];
        var cellCount = Read16(headerOffset + 3);
        var headerSize = pageType == 0x05 ? 12 : 8;
        var cellOffsets = Enumerable.Range(0, cellCount)
            .Select(index => pageStart + Read16(headerOffset + headerSize + (2 * index)))
            .ToList();
        var cells = pageType == 0x0d
            ? cellOffsets.Select((offset, index) => DescribeLeafCell(pageNumber, index, offset)).ToList()
            : cellOffsets.Select((offset, index) => new PageCell(index, offset - pageStart, null, null, [])).ToList();
        var children = pageType == 0x05
            ? cellOffsets.Select(offset => checked((int)Read32(offset))).Append(checked((int)Read32(headerOffset + 8))).ToList()
            : [];
        var previewLength = Math.Min(maximumPreviewBytes, PageSize);

        return new PageInspection(
            pageNumber,
            PageTypeName(pageType),
            cellCount,
            children,
            cells,
            Convert.ToHexString(bytes.AsSpan(pageStart, previewLength)));
    }

    public IReadOnlyList<PageTreeNode> GetTableTree(string tableName)
    {
        var nodes = new List<PageTreeNode>();
        VisitTablePage(GetTable(tableName).RootPage, nodes, new HashSet<int>());
        return nodes;
    }

    public void Dispose() { }

    private PageCell DescribeLeafCell(int page, int index, int offset)
    {
        var (payloadSize, payloadSizeBytes) = Varint(offset);
        var (rowId, rowIdBytes) = Varint(offset + payloadSizeBytes);
        var payloadOffset = offset + payloadSizeBytes + rowIdBytes;
        return new PageCell(index, offset - PageStart(page), rowId, checked((int)payloadSize), OverflowPageChain(page, payloadOffset, checked((int)payloadSize)));
    }

    private List<int> OverflowPageChain(int page, int payloadOffset, int payloadSize)
    {
        var localPayload = payloadSize <= PageSize - 35 ? payloadSize : LocalPayload(payloadSize);
        if (localPayload == payloadSize) return [];

        var overflowPages = new List<int>();
        var nextPage = checked((int)Read32(payloadOffset + localPayload));
        while (nextPage != 0)
        {
            overflowPages.Add(nextPage);
            nextPage = checked((int)Read32(PageStart(nextPage)));
        }

        return overflowPages;
    }

    private void VisitTablePage(int pageNumber, List<PageTreeNode> nodes, HashSet<int> visitedPages)
    {
        if (!visitedPages.Add(pageNumber)) throw new InvalidDataException("The table B-tree contains a page cycle.");

        var inspection = InspectPage(pageNumber);
        nodes.Add(new PageTreeNode(inspection.PageNumber, inspection.PageType, inspection.CellCount, inspection.ChildPages));
        foreach (var childPage in inspection.ChildPages)
        {
            VisitTablePage(childPage, nodes, visitedPages);
        }
    }

    private static string PageTypeName(byte pageType) => pageType switch
    {
        0x02 => "Interior index B-tree",
        0x05 => "Interior table B-tree",
        0x0a => "Leaf index B-tree",
        0x0d => "Leaf table B-tree",
        _ => $"Unknown (0x{pageType:X2})"
    };

    private IEnumerable<SqliteRow> ReadPage(int page)
    {
        var pageStart = PageStart(page);
        var header = pageStart + (page == 1 ? 100 : 0);
        var type = bytes[header];
        var count = Read16(header + 3);
        var headerSize = type == 0x05 ? 12 : 8;
        if (type == 0x05)
        {
            for (var i = 0; i < count; i++)
            {
                var cell = pageStart + Read16(header + headerSize + 2 * i);
                foreach (var row in ReadPage(checked((int)Read32(cell)))) yield return row;
            }
            foreach (var row in ReadPage(checked((int)Read32(header + 8)))) yield return row;
            yield break;
        }
        if (type != 0x0d) throw new NotSupportedException($"Page {page} is not a table B-tree page.");
        for (var i = 0; i < count; i++)
        {
            var cell = pageStart + Read16(header + headerSize + 2 * i);
            var (size, sizeBytes) = Varint(cell);
            var (rowId, idBytes) = Varint(cell + sizeBytes);
            yield return new SqliteRow(Record(Payload(page, cell + sizeBytes + idBytes, checked((int)size))), rowId);
        }
    }

    private byte[] Payload(int page, int offset, int size)
    {
        // SQLite stores large payloads across an initial page and a linked overflow-page chain.
        var max = PageSize - 35;
        var local = size <= max ? size : LocalPayload(size);
        if (local == size) return bytes[offset..(offset + size)];
        var result = new byte[size];
        Buffer.BlockCopy(bytes, offset, result, 0, local);
        var next = checked((int)Read32(offset + local));
        for (var copied = local; next != 0 && copied < size;)
        {
            var start = PageStart(next);
            var amount = Math.Min(PageSize - 4, size - copied);
            Buffer.BlockCopy(bytes, start + 4, result, copied, amount);
            copied += amount;
            next = checked((int)Read32(start));
        }
        return result;
    }

    private int LocalPayload(int size)
    {
        // This is SQLite's formula for deciding how much of a large payload remains on the B-tree page.
        var minimum = ((PageSize - 12) * 32 / 255) - 23;
        var candidate = minimum + ((size - minimum) % (PageSize - 4));
        return candidate <= PageSize - 35 ? candidate : minimum;
    }

    private static List<object?> Record(byte[] payload)
    {
        var (headerSize, headerBytes) = Varint(payload, 0);
        var types = new List<ulong>();
        for (var position = headerBytes; position < (int)headerSize;)
        {
            var (type, consumed) = Varint(payload, position);
            types.Add(type);
            position += consumed;
        }
        var values = new List<object?>();
        var body = (int)headerSize;
        foreach (var type in types) values.Add(Value(payload, ref body, type));
        return values;
    }

    private static object? Value(byte[] payload, ref int position, ulong type)
    {
        var length = type switch
        {
            0 or 8 or 9 => 0,
            1 => 1,
            2 => 2,
            3 => 3,
            4 => 4,
            5 => 6,
            6 or 7 => 8,
            >= 12 => checked((int)((type - (type % 2 == 0 ? 12UL : 13UL)) / 2)),
            _ => throw new NotSupportedException($"SQLite serial type {type} is reserved.")
        };
        if (position + length > payload.Length) throw new InvalidDataException("Record extends beyond its payload.");
        object? result = type switch
        {
            0 => null,
            1 or 2 or 3 or 4 or 5 or 6 => Integer(payload.AsSpan(position, length)),
            7 => BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64BigEndian(payload.AsSpan(position, 8))),
            8 => 0L,
            9 => 1L,
            >= 12 when type % 2 == 0 => payload.AsSpan(position, length).ToArray(),
            >= 13 => Encoding.UTF8.GetString(payload, position, length),
            _ => throw new NotSupportedException($"SQLite serial type {type} is reserved.")
        };
        position += length;
        return result;
    }

    private static long Integer(ReadOnlySpan<byte> value)
    {
        if (value.Length == 8) return BinaryPrimitives.ReadInt64BigEndian(value);
        long result = 0;
        foreach (var part in value) result = (result << 8) | part;
        var bits = value.Length * 8;
        return (result & (1L << (bits - 1))) == 0 ? result : result - (1L << bits);
    }

    private static List<string> Columns(string sql)
    {
        var start = sql.IndexOf('(');
        var end = sql.LastIndexOf(')');
        if (start < 0 || end <= start) return [];
        return sql[(start + 1)..end].Split(',').Select(x => x.Trim()).Where(x => x.Length > 0)
            .Where(x => !x.StartsWith("constraint ", StringComparison.OrdinalIgnoreCase) && !x.StartsWith("primary key", StringComparison.OrdinalIgnoreCase)
                && !x.StartsWith("unique ", StringComparison.OrdinalIgnoreCase) && !x.StartsWith("check ", StringComparison.OrdinalIgnoreCase)
                && !x.StartsWith("foreign key", StringComparison.OrdinalIgnoreCase))
            .Select(x => Unquote(x.Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries)[0])).ToList();
    }

    private int PageStart(int page)
    {
        if (page < 1) throw new InvalidDataException($"Invalid page {page}.");
        var start = checked((page - 1) * PageSize);
        if (start + PageSize > bytes.Length) throw new InvalidDataException($"Page {page} lies outside the database file.");
        return start;
    }

    private ushort Read16(int offset) => BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(offset, 2));
    private uint Read32(int offset) => BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset, 4));
    private (ulong, int) Varint(int offset) => Varint(bytes, offset);
    private static (ulong, int) Varint(byte[] source, int offset)
    {
        ulong value = 0;
        for (var i = 0; i < 9; i++)
        {
            if (offset + i >= source.Length) throw new InvalidDataException("Varint extends beyond available data.");
            var current = source[offset + i];
            if (i == 8) return ((value << 8) | current, 9);
            value = (value << 7) | (uint)(current & 0x7f);
            if ((current & 0x80) == 0) return (value, i + 1);
        }
        throw new InvalidDataException("Invalid varint.");
    }

    internal static string Unquote(string value) => value.Trim() switch
    {
        ['"', .. var content, '"'] => content.Replace("\"\"", "\""),
        ['[', .. var content, ']'] => content,
        var raw => raw
    };
}

public sealed record TableDefinition(string Name, int RootPage, string Sql, IReadOnlyList<string> Columns);
public sealed record DatabaseOverview(int PageSize, int PageCount, long FileSizeBytes, string TextEncoding, IReadOnlyList<TableDefinition> Tables);
public sealed record QueryResult(IReadOnlyList<string> Columns, IReadOnlyList<IReadOnlyList<string>> Rows, bool Truncated);
public sealed record PageCell(int Index, int Offset, ulong? RowId, int? PayloadSize, IReadOnlyList<int> OverflowPages);
public sealed record PageInspection(int PageNumber, string PageType, int CellCount, IReadOnlyList<int> ChildPages, IReadOnlyList<PageCell> Cells, string HexPreview);
public sealed record PageTreeNode(int PageNumber, string PageType, int CellCount, IReadOnlyList<int> ChildPages);
internal sealed record Schema(string Type, string Name, int RootPage, string Sql);

public sealed class SqliteRow(IReadOnlyList<object?> values, ulong? rowId = null)
{
    public string Text(int index) => Format(values.ElementAtOrDefault(index));
    public string ValueFor(string column, IReadOnlyList<string> columns) => Format(Raw(column, columns));
    public object? Raw(string column, IReadOnlyList<string> columns)
    {
        var index = columns.ToList().FindIndex(x => x.Equals(SqliteDatabase.Unquote(column), StringComparison.OrdinalIgnoreCase));
        if (index < 0) throw new ArgumentException($"No column named '{column}' exists in this table.");
        var value = values.ElementAtOrDefault(index);
        return value is null && rowId.HasValue && columns[index].Equals("id", StringComparison.OrdinalIgnoreCase)
            ? checked((long)rowId.Value)
            : value;
    }
    internal static string Format(object? value) => value switch
    {
        null => "",
        byte[] blob => $"X'{Convert.ToHexString(blob)}'",
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? ""
    };
}
