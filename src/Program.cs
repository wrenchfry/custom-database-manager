using CodecraftersSqlite;

if (args.Length < 2)
{
    Console.Error.WriteLine("Usage: sqlite-clone <database path> <.command | SELECT query>");
    return 1;
}

try
{
    using var database = new SqliteDatabase(args[0]);
    var command = string.Join(' ', args.Skip(1)).Trim();
    switch (command.ToLowerInvariant())
    {
        case ".dbinfo":
            Console.WriteLine($"database page size: {database.PageSize}");
            Console.WriteLine($"number of tables: {database.Tables.Count}");
            break;
        case ".tables":
            Console.WriteLine(string.Join(' ', database.Tables.Select(x => x.Name)));
            break;
        case ".schema":
            foreach (var schemaTable in database.Tables) Console.WriteLine(schemaTable.Sql);
            break;
        default:
            var query = SelectQuery.Parse(command);
            var table = database.GetTable(query.Table);
            var rows = database.ReadRows(table).Where(row => query.Filter is null || query.Filter.Matches(row, table.Columns));
            if (query.IsCount) Console.WriteLine(rows.Count());
            else
            {
                var columns = query.Columns is ["*"] ? table.Columns : query.Columns;
                foreach (var row in rows) Console.WriteLine(string.Join("|", columns.Select(x => row.ValueFor(x, table.Columns))));
            }
            break;
    }
}
catch (Exception error) when (error is ArgumentException or InvalidDataException or NotSupportedException)
{
    Console.Error.WriteLine($"Error: {error.Message}");
    return 1;
}

return 0;
