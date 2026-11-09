# SQLite Under the Hood

A read-only SQLite file explorer built around a custom C# parser. It examines database headers, tables, records, B-tree pages, and overflow pages without using a SQLite library to read the database.

## What it supports

- Inspect SQLite database and table information.
- Read ordinary table B-trees and overflow records.
- Run a limited, read-only `SELECT` subset: column selection, `count(*)`, and one equality filter.
- Try the included `sample.db`.

This is an educational explorer, not a complete SQLite implementation. Files are limited to 10 MB, query results to 500 rows, and unsupported features include joins, sorting, grouping, write statements, virtual tables, and `WITHOUT ROWID` tables.

## Tests

```powershell
dotnet test SqliteUnderTheHood.sln --configuration Release
```
