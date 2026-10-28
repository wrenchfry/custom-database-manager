namespace CodecraftersSqlite;

public sealed class ExplorerStore
{
    private const long MaximumUploadBytes = 10 * 1024 * 1024;
    private static readonly HashSet<string> SupportedExtensions = [".db", ".sqlite", ".sqlite3"];
    private readonly string uploadsDirectory;
    private readonly Lock sync = new();
    private string? currentPath;
    private string? currentFileName;

    public ExplorerStore(IHostEnvironment environment)
    {
        uploadsDirectory = Path.Combine(environment.ContentRootPath, "data", "uploads");
        Directory.CreateDirectory(uploadsDirectory);

        // Uploaded databases are temporary read-only inspection copies, not user data to retain.
        foreach (var uploadedFile in Directory.EnumerateFiles(uploadsDirectory))
        {
            File.Delete(uploadedFile);
        }
    }

    public async Task<ExplorerOverview> LoadAsync(IFormFile file, CancellationToken cancellationToken)
    {
        ValidateUpload(file);
        var temporaryPath = Path.Combine(uploadsDirectory, $"{Guid.NewGuid():N}.upload");

        try
        {
            await using (var destination = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.SequentialScan))
            {
                await file.CopyToAsync(destination, cancellationToken);
            }

            using var database = new SqliteDatabase(temporaryPath);
            var overview = new ExplorerOverview(Path.GetFileName(file.FileName), database.Overview());

            lock (sync)
            {
                var previousPath = currentPath;
                currentPath = temporaryPath;
                currentFileName = overview.FileName;
                if (previousPath is not null && File.Exists(previousPath))
                {
                    File.Delete(previousPath);
                }
            }

            return overview;
        }
        catch
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }

            throw;
        }
    }

    public ExplorerOverview Overview()
    {
        using var database = OpenDatabase(out var fileName);
        return new ExplorerOverview(fileName, database.Overview());
    }

    public QueryResult RunQuery(string query) => OpenDatabase(out _).Use(database => database.RunQuery(query));

    public PageInspection InspectPage(int pageNumber) => OpenDatabase(out _).Use(database => database.InspectPage(pageNumber));

    public IReadOnlyList<PageTreeNode> GetTableTree(string tableName) => OpenDatabase(out _).Use(database => database.GetTableTree(tableName));

    private SqliteDatabase OpenDatabase(out string fileName)
    {
        lock (sync)
        {
            if (currentPath is null || currentFileName is null)
            {
                throw new InvalidOperationException("Upload a SQLite database before using the explorer.");
            }

            fileName = currentFileName;
            return new SqliteDatabase(currentPath);
        }
    }

    private static void ValidateUpload(IFormFile file)
    {
        if (file.Length == 0)
        {
            throw new ArgumentException("Choose a non-empty database file.");
        }

        if (file.Length > MaximumUploadBytes)
        {
            throw new ArgumentException("Database files must be 10 MB or smaller.");
        }

        var extension = Path.GetExtension(file.FileName);
        if (!SupportedExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Choose a .db, .sqlite, or .sqlite3 file.");
        }
    }
}

public sealed record ExplorerOverview(string FileName, DatabaseOverview Database);

internal static class SqliteDatabaseExtensions
{
    public static TResult Use<TResult>(this SqliteDatabase database, Func<SqliteDatabase, TResult> action)
    {
        using (database)
        {
            return action(database);
        }
    }
}
