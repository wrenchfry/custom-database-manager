# SQLite Under the Hood

A read-only SQLite file explorer built around a custom C# parser. It examines database headers, tables, records, B-tree pages, and overflow pages without using a SQLite library to read the database.

## Try the demo

Open the [live demo](https://custom-database-manager.onrender.com/) and either download the sample database from the page or choose your own `.db`, `.sqlite`, or `.sqlite3` file (up to 10 MB).

1. Choose a file and select **Open database**.
2. Select a table from **Tables**. Its suggested query runs automatically.
3. Edit the query if you like, then select **Run query**.
4. Select **Storage pages**, then **Inspect page** to see how SQLite stores the table.

The reader is read-only. Queries support selected columns, `count(*)`, and one equality filter; results are limited to 500 rows. This public demo has no login, so do not upload sensitive files.

This is an educational explorer, not a complete SQLite client. It focuses on ordinary table B-trees; joins, sorting, grouping, write statements, virtual tables, and `WITHOUT ROWID` tables are not supported.

## Tests

```powershell
dotnet test SqliteUnderTheHood.sln --configuration Release
```
