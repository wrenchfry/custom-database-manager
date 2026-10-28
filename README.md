# SQLite Under the Hood

SQLite Under the Hood is a local, read-only explorer for SQLite database files. It was built from the Codecrafters **Build Your Own SQLite** challenge and uses a custom parser to inspect uploaded databases rather than using SQLite to read them.

It is an educational file-format explorer, not a replacement for SQLite or a complete database management system.

## What it does

- Opens local `.db`, `.sqlite`, and `.sqlite3` files up to 10 MB.
- Reads the SQLite header to show page size, page count, text encoding, and file size.
- Discovers user tables from the `sqlite_schema` table.
- Traverses table B-tree interior and leaf pages.
- Decodes table records, SQLite varints, serial types, and overflow payload pages.
- Shows a selected table’s B-tree pages and child-page links.
- Inspects individual pages, including cell details, row IDs, overflow-page chains, and a bounded hexadecimal preview.
- Runs a deliberately limited, read-only subset of SQL: `SELECT`, selected columns or `*`, `count(*)`, and one equality `WHERE` condition.

## What it does not do

- Write, update, delete, or otherwise modify a database.
- Support joins, indexes, transactions, arbitrary SQL, or all SQLite B-tree/page types.
- Attempt to recover corrupt databases.
- Use `Microsoft.Data.Sqlite`, a SQLite driver, or a database library to read the file being explored.

## Architecture

```text
SQLite file upload
        ↓
Custom SQLite header / schema parser
        ↓
Table B-tree traversal + record decoding
        ↓
Metadata, page inspector, B-tree view, limited SELECT results
```

## Run locally

Prerequisites: .NET SDK 9.0 or later.

```bash
dotnet run
```

Open the local URL printed by ASP.NET Core, choose a SQLite file, and explore it in the browser. Uploaded files are temporarily stored only for the current local explorer session and are never modified by the application.

## CLI inspector

The same executable also exposes the reader through a small command-line interface:

```bash
dotnet run -- sample.db .dbinfo
dotnet run -- sample.db .tables
dotnet run -- sample.db ".schema"
dotnet run -- sample.db "SELECT id, name FROM apples WHERE id = 1"
```

## Tests and quality checks

```bash
dotnet test
dotnet format codecrafters-sqlite.sln whitespace --verify-no-changes
```

The tests cover query parsing, filters, valid/invalid database files, metadata, query execution, page inspection, and B-tree traversal.

## Project structure

```text
src/       Custom SQLite parser, query parser, explorer API
wwwroot/   Browser interface for the explorer
tests/     Automated unit tests
sample.db  Sample SQLite database for the CLI and test suite
```
