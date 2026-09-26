# Research Notebook

Research Notebook is a local-first study workspace for collecting notes, organising them with tags, and turning ideas into flashcards for active recall. Its data is stored in a SQLite database, and the app includes a small custom SQLite page reader that can inspect that database.

## Features

- Create and edit notes with titles, content, and comma-separated tags.
- Search across saved notes.
- Create flashcards from a note and review cards that are due.
- Use simple review ratings to schedule the next review.
- Inspect the local SQLite database's tables, page size, columns, and row counts.
- Run the included command-line SQLite inspector for `.dbinfo`, `.tables`, `.schema`, and basic `SELECT` queries.

## Tech stack

- ASP.NET Core / .NET 10
- SQLite via `Microsoft.Data.Sqlite`
- Vanilla HTML, CSS, and JavaScript

## Getting started

### Prerequisites

- .NET SDK 10.0 or later

### Run the web app

```bash
dotnet run
```

Open the local URL printed by ASP.NET Core. On first launch, the application creates `data/notebook.db` and adds a welcome note.

### Use the SQLite inspector

The same executable also accepts a database file and a command:

```bash
dotnet run -- sample.db .dbinfo
dotnet run -- sample.db .tables
dotnet run -- sample.db ".schema"
dotnet run -- sample.db "SELECT id, name FROM apples WHERE id = 1"
```

The custom reader supports table B-tree interior and leaf pages, overflow payload pages, selected columns or `*`, `count(*)`, and a single equality `WHERE` condition. It is intentionally read-only and is not a complete SQLite implementation.

## Project structure

```text
src/             ASP.NET Core endpoints and SQLite reader
wwwroot/         Browser interface
data/            Runtime SQLite database (ignored by Git)
sample.db        Sample database for the command-line inspector
```

## Data and privacy

Notes and flashcards are stored locally in `data/notebook.db`. The database is excluded from version control, so your study data is not committed with the project.
