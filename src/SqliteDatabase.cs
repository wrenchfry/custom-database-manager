using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace SqliteUnderTheHood;

/// <summary>
/// Reads the SQLite file format needed by the inspector without using a SQLite driver.
/// The implementation is intentionally read-only and supports a focused subset of page types.
/// </summary>
public sealed class SqliteDatabase : IDisposable
{
    private const int MaximumFileBytes = 10 * 1024 * 1024;
    private readonly byte[] bytes;
    private readonly List<TableDefinition> tables;
    private readonly int usablePageSize;
    private readonly Encoding encoding;
    public int PageSize { get; }
    public int PageCount => bytes.Length / PageSize;
    public long FileSizeBytes => bytes.LongLength;
    public string TextEncoding { get; }
    public IReadOnlyList<TableDefinition> Tables => tables;

    public SqliteDatabase(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        if (stream.Length is < 100 or > MaximumFileBytes)
        {
            throw new InvalidDataException("SQLite files must be between 100 bytes and 10 MB.");
        }

        bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        if (Encoding.ASCII.GetString(bytes, 0, 16) != "SQLite format 3\0")
        {
            throw new InvalidDataException("The file is not a SQLite 3 database.");
        }

        PageSize = Read16(16) is 1 ? 65536 : Read16(16);
        if (PageSize < 512 || PageSize > 65536 || PageSize % 2 != 0)
        {
            throw new InvalidDataException("Invalid page size.");
        }

        if (bytes.Length % PageSize != 0)
        {
            throw new InvalidDataException("The database file does not contain complete pages.");
        }

        if (bytes[18] is not (1 or 2) || bytes[19] is not (1 or 2))
        {
            throw new InvalidDataException("Unsupported SQLite file format version.");
        }

        if (bytes[21] != 64 || bytes[22] != 32 || bytes[23] != 32)
        {
            throw new InvalidDataException("Invalid SQLite payload fractions.");
        }

        usablePageSize = PageSize - bytes[20];
        if (usablePageSize < 480)
        {
            throw new InvalidDataException("Invalid SQLite reserved-page size.");
        }

        var encodingId = Read32(56);
        encoding = encodingId switch
        {
            0 or 1 => Encoding.UTF8,
            2 => Encoding.Unicode,
            3 => Encoding.BigEndianUnicode,
            _ => throw new InvalidDataException("Invalid SQLite text encoding.")
        };

        TextEncoding = encodingId switch
        {
            0 or 1 => "UTF-8",
            2 => "UTF-16 little-endian",
            3 => "UTF-16 big-endian",
            _ => throw new InvalidDataException("Invalid SQLite text encoding.")
        };

        var declaredPageCount = Read32(28);
        if (declaredPageCount > PageCount)
        {
            throw new InvalidDataException("The SQLite header refers to pages beyond the end of the file.");
        }

        tables = ReadPage(1)
            .Select(row => new Schema(row.Text(0), row.Text(1), int.TryParse(row.Text(3), out var root) ? root : 0, row.Text(4)))
            .Where(x => x.Type.Equals("table", StringComparison.OrdinalIgnoreCase) && !x.Name.StartsWith("sqlite_", StringComparison.OrdinalIgnoreCase))
            .Where(x => x.RootPage > 0)
            .Select(x => new TableDefinition(x.Name, x.RootPage, x.Sql, Columns(x.Sql), RowIdColumn(x.Sql))).ToList();
    }

    public TableDefinition GetTable(string name) => tables.FirstOrDefault(x => x.Name.Equals(Unquote(name), StringComparison.OrdinalIgnoreCase))
        ?? throw new ArgumentException($"No table named '{name}' exists in this database.");
    public IEnumerable<SqliteRow> ReadRows(TableDefinition table) => ReadPage(table.RootPage, table.RowIdColumn);
    public DatabaseOverview Overview() => new(PageSize, PageCount, FileSizeBytes, TextEncoding, Tables);

    public QueryResult RunQuery(string queryText, int maximumRows = 500)
    {
        if (maximumRows is < 1 or > 1_000) throw new ArgumentOutOfRangeException(nameof(maximumRows));

        var query = SelectQuery.Parse(queryText);
        var table = GetTable(query.Table);
        var columns = query.Columns is ["*"] ? table.Columns : query.Columns;
        if (!query.IsCount)
        {
            foreach (var column in columns)
            {
                if (!table.Columns.Contains(column, StringComparer.OrdinalIgnoreCase))
                {
                    throw new ArgumentException($"No column named '{column}' exists in table '{table.Name}'.");
                }
            }
        }

        if (query.Filter is not null && !table.Columns.Contains(query.Filter.Column, StringComparer.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"No column named '{query.Filter.Column}' exists in table '{table.Name}'.");
        }

        var rows = ReadRows(table).Where(row => query.Filter is null || query.Filter.Matches(row, table.Columns));

        if (query.IsCount)
        {
            return new QueryResult(["count(*)"], [[rows.Count().ToString(CultureInfo.InvariantCulture)]], false);
        }

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
        var pageHeader = ReadBtreeHeader(pageNumber);
        var cells = pageHeader.PageType == 0x0d
            ? pageHeader.CellOffsets.Select((offset, index) => DescribeLeafCell(pageNumber, index, offset)).ToList()
            : pageHeader.CellOffsets.Select((offset, index) => new PageCell(index, offset - PageStart(pageNumber), null, null, [])).ToList();
        var children = pageHeader.PageType == 0x05 ? pageHeader.ChildPages : [];
        var previewLength = Math.Min(maximumPreviewBytes, PageSize);

        return new PageInspection(
            pageNumber,
            PageTypeName(pageHeader.PageType),
            pageHeader.CellOffsets.Count,
            children,
            cells,
            Convert.ToHexString(bytes.AsSpan(pageStart, previewLength)));
    }

    public IReadOnlyList<PageTreeNode> GetTableTree(string tableName)
    {
        var nodes = new List<PageTreeNode>();
        var pendingPages = new Stack<int>();
        var visitedPages = new HashSet<int>();
        pendingPages.Push(GetTable(tableName).RootPage);

        while (pendingPages.TryPop(out var pageNumber))
        {
            if (!visitedPages.Add(pageNumber))
            {
                throw new InvalidDataException("The table B-tree contains a page cycle or duplicate child page.");
            }

            var pageHeader = ReadBtreeHeader(pageNumber);
            nodes.Add(new PageTreeNode(pageNumber, PageTypeName(pageHeader.PageType), pageHeader.CellOffsets.Count, pageHeader.ChildPages));
            for (var index = pageHeader.ChildPages.Count - 1; index >= 0; index--)
            {
                pendingPages.Push(pageHeader.ChildPages[index]);
            }
        }

        return nodes;
    }

    public void Dispose() { }

    private PageCell DescribeLeafCell(int page, int index, int offset)
    {
        var pageStart = PageStart(page);
        var pageEnd = pageStart + usablePageSize;
        var (payloadSize, payloadSizeBytes) = Varint(offset, pageEnd);
        var (rowId, rowIdBytes) = Varint(checked(offset + payloadSizeBytes), pageEnd);
        var payloadOffset = checked(offset + payloadSizeBytes + rowIdBytes);
        var checkedSize = CheckedPayloadSize(payloadSize);
        var localPayload = checkedSize <= usablePageSize - 35 ? checkedSize : LocalPayload(checkedSize);
        EnsureRange(payloadOffset, localPayload, pageEnd, "Record payload extends beyond its B-tree page.");
        if (localPayload < checkedSize)
        {
            EnsureRange(payloadOffset + localPayload, sizeof(uint), pageEnd, "Overflow-page pointer extends beyond its B-tree page.");
        }

        var overflow = localPayload == checkedSize
            ? []
            : OverflowPageChain(payloadOffset + localPayload, checkedSize - localPayload);
        return new PageCell(index, offset - pageStart, unchecked((long)rowId), checkedSize, overflow);
    }

    private List<int> OverflowPageChain(int pointerOffset, int remaining)
    {
        var overflowPages = new List<int>();
        var nextPage = ReadPageReference(pointerOffset);
        var visitedPages = new HashSet<int>();
        while (remaining > 0)
        {
            if (nextPage == 0)
            {
                throw new InvalidDataException("The overflow-page chain ends before the record payload is complete.");
            }

            if (!visitedPages.Add(nextPage))
            {
                throw new InvalidDataException("The overflow-page chain contains a cycle.");
            }

            overflowPages.Add(nextPage);
            var start = PageStart(nextPage);
            remaining -= Math.Min(usablePageSize - 4, remaining);
            nextPage = remaining > 0 ? ReadPageReference(start) : 0;
        }

        return overflowPages;
    }

    private static string PageTypeName(byte pageType) => pageType switch
    {
        0x02 => "Interior index B-tree",
        0x05 => "Interior table B-tree",
        0x0a => "Leaf index B-tree",
        0x0d => "Leaf table B-tree",
        _ => $"Unknown (0x{pageType:X2})"
    };

    private BtreePageHeader ReadBtreeHeader(int pageNumber)
    {
        var pageStart = PageStart(pageNumber);
        var headerOffset = pageStart + (pageNumber == 1 ? 100 : 0);
        EnsureRange(headerOffset, 8, pageStart + usablePageSize, "B-tree header extends beyond its page.");
        var pageType = bytes[headerOffset];
        if (pageType is not (0x02 or 0x05 or 0x0a or 0x0d))
        {
            throw new InvalidDataException($"Page {pageNumber} has an invalid SQLite B-tree page type.");
        }

        var isInterior = pageType is 0x02 or 0x05;
        var headerSize = isInterior ? 12 : 8;
        EnsureRange(headerOffset, headerSize, pageStart + usablePageSize, "B-tree header extends beyond its page.");
        var cellCount = Read16(headerOffset + 3);
        var pointerArray = checked(headerOffset + headerSize);
        var pointerArrayBytes = checked(cellCount * sizeof(ushort));
        EnsureRange(pointerArray, pointerArrayBytes, pageStart + usablePageSize, "B-tree cell pointers extend beyond their page.");
        var firstCellOffset = checked(pointerArray + pointerArrayBytes);
        var cellOffsets = new List<int>(cellCount);
        var distinctCellOffsets = new HashSet<int>();
        var childPages = new List<int>(isInterior ? cellCount + 1 : 0);

        for (var index = 0; index < cellCount; index++)
        {
            var relativeOffset = Read16(pointerArray + index * sizeof(ushort));
            if (relativeOffset < firstCellOffset - pageStart || relativeOffset >= usablePageSize)
            {
                throw new InvalidDataException($"Cell {index} on page {pageNumber} has an invalid offset.");
            }

            var cellOffset = checked(pageStart + relativeOffset);
            EnsureRange(cellOffset, isInterior ? sizeof(uint) : 1, pageStart + usablePageSize, "B-tree cell extends beyond its page.");
            if (!distinctCellOffsets.Add(cellOffset))
            {
                throw new InvalidDataException($"Page {pageNumber} contains duplicate cell pointers.");
            }

            cellOffsets.Add(cellOffset);
            if (isInterior)
            {
                childPages.Add(ReadRequiredPageReference(cellOffset));
            }
        }

        if (isInterior)
        {
            childPages.Add(ReadRequiredPageReference(headerOffset + 8));
        }

        return new BtreePageHeader(pageType, cellOffsets, childPages);
    }

    private IEnumerable<SqliteRow> ReadPage(int page, string? rowIdColumn = null)
    {
        var pendingPages = new Stack<int>();
        var visitedPages = new HashSet<int>();
        pendingPages.Push(page);

        while (pendingPages.TryPop(out var pageNumber))
        {
            if (!visitedPages.Add(pageNumber))
            {
                throw new InvalidDataException("The table B-tree contains a page cycle or duplicate child page.");
            }

            var pageHeader = ReadBtreeHeader(pageNumber);
            if (pageHeader.PageType == 0x05)
            {
                for (var index = pageHeader.ChildPages.Count - 1; index >= 0; index--)
                {
                    pendingPages.Push(pageHeader.ChildPages[index]);
                }

                continue;
            }

            if (pageHeader.PageType != 0x0d)
            {
                throw new NotSupportedException($"Page {pageNumber} is not a table B-tree page.");
            }

            foreach (var cell in pageHeader.CellOffsets)
            {
                var cellEnd = PageStart(pageNumber) + usablePageSize;
                var (size, sizeBytes) = Varint(cell, cellEnd);
                var (rowId, idBytes) = Varint(checked(cell + sizeBytes), cellEnd);
                var payloadOffset = checked(cell + sizeBytes + idBytes);
                var payloadSize = CheckedPayloadSize(size);
                yield return new SqliteRow(Record(Payload(pageNumber, payloadOffset, payloadSize)), unchecked((long)rowId), rowIdColumn);
            }
        }
    }

    private byte[] Payload(int page, int offset, int size)
    {
        var pageEnd = PageStart(page) + usablePageSize;
        var max = usablePageSize - 35;
        var local = size <= max ? size : LocalPayload(size);
        EnsureRange(offset, local, pageEnd, "Record payload extends beyond its B-tree page.");
        if (local == size) return bytes[offset..(offset + size)];

        var result = new byte[size];
        Buffer.BlockCopy(bytes, offset, result, 0, local);
        var copied = local;
        EnsureRange(offset + local, sizeof(uint), pageEnd, "Overflow-page pointer extends beyond its B-tree page.");
        var nextPage = ReadPageReference(offset + local);
        var visitedPages = new HashSet<int>();
        while (copied < size)
        {
            if (nextPage == 0)
            {
                throw new InvalidDataException("The overflow-page chain ends before the record payload is complete.");
            }

            if (!visitedPages.Add(nextPage))
            {
                throw new InvalidDataException("The overflow-page chain contains a cycle.");
            }

            var start = PageStart(nextPage);
            var amount = Math.Min(usablePageSize - 4, size - copied);
            EnsureRange(start + 4, amount, start + usablePageSize, "Overflow payload extends beyond its page.");
            Buffer.BlockCopy(bytes, start + 4, result, copied, amount);
            copied += amount;
            nextPage = copied < size ? ReadPageReference(start) : 0;
        }

        return result;
    }

    private int LocalPayload(int size)
    {
        // This is SQLite's formula for deciding how much of a large payload remains on the B-tree page.
        var minimum = ((usablePageSize - 12) * 32 / 255) - 23;
        var candidate = minimum + ((size - minimum) % (usablePageSize - 4));
        return candidate <= usablePageSize - 35 ? candidate : minimum;
    }

    private List<object?> Record(byte[] payload)
    {
        var (headerSize, headerBytes) = Varint(payload, 0);
        if (headerSize < (ulong)headerBytes || headerSize > (ulong)payload.Length)
        {
            throw new InvalidDataException("Record header extends beyond its payload.");
        }

        var types = new List<ulong>();
        var position = headerBytes;
        while ((ulong)position < headerSize)
        {
            var (type, consumed) = Varint(payload, position);
            types.Add(type);
            position = checked(position + consumed);
            if ((ulong)position > headerSize)
            {
                throw new InvalidDataException("Record serial types extend beyond the record header.");
            }
        }

        var values = new List<object?>();
        var body = (int)headerSize;
        foreach (var type in types) values.Add(Value(payload, ref body, type));
        return values;
    }

    private object? Value(byte[] payload, ref int position, ulong type)
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
        if (length > payload.Length - position) throw new InvalidDataException("Record extends beyond its payload.");
        object? result = type switch
        {
            0 => null,
            1 or 2 or 3 or 4 or 5 or 6 => Integer(payload.AsSpan(position, length)),
            7 => BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64BigEndian(payload.AsSpan(position, 8))),
            8 => 0L,
            9 => 1L,
            >= 12 when type % 2 == 0 => payload.AsSpan(position, length).ToArray(),
            >= 13 => encoding.GetString(payload, position, length),
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
        return TableDefinitions(sql)
            .Select(definition => TryReadIdentifier(definition, out var name, out _) ? name : null)
            .Where(name => name is not null)
            .Cast<string>()
            .Where(name => !IsTableConstraint(name))
            .ToList();
    }

    private static string? RowIdColumn(string sql)
    {
        if (sql.Contains("WITHOUT ROWID", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        foreach (var definition in TableDefinitions(sql))
        {
            if (TryReadIdentifier(definition, out var name, out var identifierLength)
                && !IsTableConstraint(name)
                && Regex.IsMatch(
                    definition[identifierLength..],
                    "^\\s+INTEGER\\s+PRIMARY\\s+KEY(?!\\s+DESC\\b)",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                    TimeSpan.FromMilliseconds(100)))
            {
                return name;
            }
        }

        return null;
    }

    private static List<string> TableDefinitions(string sql)
    {
        var openingParenthesis = sql.IndexOf('(');
        if (openingParenthesis < 0)
        {
            return [];
        }

        var definitions = new List<string>();
        var definitionStart = openingParenthesis + 1;
        var nestedParentheses = 0;
        var quoteEnd = '\0';
        for (var index = definitionStart; index < sql.Length; index++)
        {
            var character = sql[index];
            if (quoteEnd != '\0')
            {
                if (character == quoteEnd)
                {
                    if (index + 1 < sql.Length && sql[index + 1] == quoteEnd)
                    {
                        index++;
                    }
                    else
                    {
                        quoteEnd = '\0';
                    }
                }

                continue;
            }

            if (character is '\'' or '"' or '`')
            {
                quoteEnd = character;
            }
            else if (character == '[')
            {
                quoteEnd = ']';
            }
            else if (character == '(')
            {
                nestedParentheses++;
            }
            else if (character == ')')
            {
                if (nestedParentheses == 0)
                {
                    definitions.Add(sql[definitionStart..index]);
                    return definitions;
                }

                nestedParentheses--;
            }
            else if (character == ',' && nestedParentheses == 0)
            {
                definitions.Add(sql[definitionStart..index]);
                definitionStart = index + 1;
            }
        }

        throw new InvalidDataException("A table definition has unbalanced parentheses or quotes.");
    }

    private static bool TryReadIdentifier(string definition, out string name, out int identifierLength)
    {
        var start = definition.Length - definition.TrimStart().Length;
        if (start == definition.Length)
        {
            name = string.Empty;
            identifierLength = 0;
            return false;
        }

        var first = definition[start];
        if (first is '"' or '`' or '[')
        {
            var endQuote = first == '[' ? ']' : first;
            var end = definition.IndexOf(endQuote, start + 1);
            while (end >= 0 && endQuote != ']' && end + 1 < definition.Length && definition[end + 1] == endQuote)
            {
                end = definition.IndexOf(endQuote, end + 2);
            }

            if (end < 0)
            {
                throw new InvalidDataException("A table definition contains an unterminated quoted column name.");
            }

            identifierLength = end + 1;
            name = Unquote(definition[start..identifierLength]);
            return name.Length > 0;
        }

        var tokenEnd = start;
        while (tokenEnd < definition.Length && !char.IsWhiteSpace(definition[tokenEnd]))
        {
            tokenEnd++;
        }

        identifierLength = tokenEnd;
        name = Unquote(definition[start..tokenEnd]);
        return name.Length > 0;
    }

    private static bool IsTableConstraint(string name) =>
        name.Equals("CONSTRAINT", StringComparison.OrdinalIgnoreCase)
        || name.Equals("PRIMARY", StringComparison.OrdinalIgnoreCase)
        || name.Equals("UNIQUE", StringComparison.OrdinalIgnoreCase)
        || name.Equals("CHECK", StringComparison.OrdinalIgnoreCase)
        || name.Equals("FOREIGN", StringComparison.OrdinalIgnoreCase);

    private int PageStart(int page)
    {
        if (page < 1 || page > PageCount)
        {
            throw new InvalidDataException($"Page {page} lies outside the database file.");
        }

        return checked((page - 1) * PageSize);
    }

    private ushort Read16(int offset)
    {
        EnsureRange(offset, sizeof(ushort), bytes.Length, "A 16-bit field extends beyond the database file.");
        return BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(offset, sizeof(ushort)));
    }

    private uint Read32(int offset)
    {
        EnsureRange(offset, sizeof(uint), bytes.Length, "A 32-bit field extends beyond the database file.");
        return BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset, sizeof(uint)));
    }

    private int ReadPageReference(int offset)
    {
        var page = Read32(offset);
        if (page == 0)
        {
            return 0;
        }

        if (page > PageCount)
        {
            throw new InvalidDataException($"Page reference {page} lies outside the database file.");
        }

        return checked((int)page);
    }

    private int ReadRequiredPageReference(int offset)
    {
        var page = ReadPageReference(offset);
        return page != 0
            ? page
            : throw new InvalidDataException("A B-tree child page reference cannot be zero.");
    }

    private static (ulong, int) Varint(byte[] source, int offset) => Varint(source, offset, source.Length);

    private (ulong, int) Varint(int offset, int end) => Varint(bytes, offset, end);

    private static (ulong, int) Varint(byte[] source, int offset, int end)
    {
        ulong value = 0;
        for (var i = 0; i < 9; i++)
        {
            if (offset < 0 || offset >= end || offset >= source.Length || i >= end - offset)
            {
                throw new InvalidDataException("Varint extends beyond available data.");
            }

            var current = source[offset + i];
            if (i == 8) return ((value << 8) | current, 9);
            value = (value << 7) | (uint)(current & 0x7f);
            if ((current & 0x80) == 0) return (value, i + 1);
        }
        throw new InvalidDataException("Invalid varint.");
    }

    private static int CheckedPayloadSize(ulong payloadSize)
    {
        if (payloadSize > MaximumFileBytes)
        {
            throw new InvalidDataException("Record payload exceeds the supported database size.");
        }

        return checked((int)payloadSize);
    }

    private static void EnsureRange(int offset, int length, int end, string message)
    {
        if (offset < 0 || length < 0 || offset > end || length > end - offset)
        {
            throw new InvalidDataException(message);
        }
    }

    private sealed record BtreePageHeader(byte PageType, IReadOnlyList<int> CellOffsets, IReadOnlyList<int> ChildPages);

    internal static string Unquote(string value) => value.Trim() switch
    {
        ['"', .. var content, '"'] => content.Replace("\"\"", "\""),
        ['[', .. var content, ']'] => content,
        ['`', .. var content, '`'] => content,
        var raw => raw
    };
}

public sealed record TableDefinition(string Name, int RootPage, string Sql, IReadOnlyList<string> Columns, string? RowIdColumn = null);
public sealed record DatabaseOverview(int PageSize, int PageCount, long FileSizeBytes, string TextEncoding, IReadOnlyList<TableDefinition> Tables);
public sealed record QueryResult(IReadOnlyList<string> Columns, IReadOnlyList<IReadOnlyList<string>> Rows, bool Truncated);
public sealed record PageCell(int Index, int Offset, long? RowId, int? PayloadSize, IReadOnlyList<int> OverflowPages);
public sealed record PageInspection(int PageNumber, string PageType, int CellCount, IReadOnlyList<int> ChildPages, IReadOnlyList<PageCell> Cells, string HexPreview);
public sealed record PageTreeNode(int PageNumber, string PageType, int CellCount, IReadOnlyList<int> ChildPages);
internal sealed record Schema(string Type, string Name, int RootPage, string Sql);

public sealed class SqliteRow(IReadOnlyList<object?> values, long? rowId = null, string? rowIdColumn = null)
{
    public string Text(int index) => Format(values.ElementAtOrDefault(index));
    public string ValueFor(string column, IReadOnlyList<string> columns) => Format(Raw(column, columns));
    public object? Raw(string column, IReadOnlyList<string> columns)
    {
        var index = columns.ToList().FindIndex(x => x.Equals(SqliteDatabase.Unquote(column), StringComparison.OrdinalIgnoreCase));
        if (index < 0) throw new ArgumentException($"No column named '{column}' exists in this table.");
        var value = values.ElementAtOrDefault(index);
        return value is null && rowId.HasValue && columns[index].Equals(rowIdColumn, StringComparison.OrdinalIgnoreCase)
            ? rowId.Value
            : value;
    }
    internal static string Format(object? value) => value switch
    {
        null => "NULL",
        byte[] blob => $"X'{Convert.ToHexString(blob)}'",
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? ""
    };
}
