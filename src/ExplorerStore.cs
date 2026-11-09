using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace SqliteUnderTheHood;

public sealed class ExplorerStore : IDisposable
{
    public const long MaximumUploadBytes = 10 * 1024 * 1024;
    public const string SessionInitializedKey = "database-open";
    private static readonly TimeSpan SessionRetention = TimeSpan.FromMinutes(30);
    private static readonly HashSet<string> SupportedExtensions = [".db", ".sqlite", ".sqlite3"];
    private readonly string uploadsDirectory;
    private readonly ConcurrentDictionary<string, SessionDatabase> sessions = new(StringComparer.Ordinal);
    private readonly Lock sync = new();

    public ExplorerStore(IHostEnvironment environment)
    {
        var applicationId = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(environment.ContentRootPath)));
        uploadsDirectory = Path.Combine(Path.GetTempPath(), "sqlite-under-the-hood", applicationId);
        Directory.CreateDirectory(uploadsDirectory);
        DeleteExpiredOrphanedUploads();
    }

    public async Task<ExplorerOverview> LoadAsync(string sessionId, IFormFile file, CancellationToken cancellationToken)
    {
        ValidateUpload(file);
        CleanupExpiredSessions();
        var session = GetOrCreateSession(sessionId);
        await session.Gate.WaitAsync(cancellationToken);
        var token = Guid.NewGuid().ToString("N");
        var temporaryPath = Path.Combine(uploadsDirectory, $"{token}.upload");
        var databasePath = Path.Combine(uploadsDirectory, $"{token}.sqlite");

        try
        {
            await using (var destination = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.SequentialScan))
            {
                await file.CopyToAsync(destination, cancellationToken);
            }

            if (new FileInfo(temporaryPath).Length > MaximumUploadBytes)
            {
                throw new ArgumentException("Database files must be 10 MB or smaller.");
            }

            using var database = new SqliteDatabase(temporaryPath);
            var overview = new ExplorerOverview(SafeFileName(file.FileName), database.Overview());
            File.Move(temporaryPath, databasePath);

            var previousPath = session.Path;
            if (previousPath is not null && File.Exists(previousPath))
            {
                File.Delete(previousPath);
            }

            session.Path = databasePath;
            session.FileName = overview.FileName;
            Touch(session);
            return overview;
        }
        catch
        {
            foreach (var path in new[] { temporaryPath, databasePath })
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }

            throw;
        }
        finally
        {
            session.Gate.Release();
        }
    }

    public ExplorerOverview Overview(string sessionId)
    {
        return UseDatabase(sessionId, (database, fileName) => new ExplorerOverview(fileName, database.Overview()));
    }

    public QueryResult RunQuery(string sessionId, string query) =>
        UseDatabase(sessionId, (database, _) => database.RunQuery(query));

    public PageInspection InspectPage(string sessionId, int pageNumber) =>
        UseDatabase(sessionId, (database, _) => database.InspectPage(pageNumber));

    public IReadOnlyList<PageTreeNode> GetTableTree(string sessionId, string tableName) =>
        UseDatabase(sessionId, (database, _) => database.GetTableTree(tableName));

    private TResult UseDatabase<TResult>(string sessionId, Func<SqliteDatabase, string, TResult> action)
    {
        var session = GetSession(sessionId);
        session.Gate.Wait();
        try
        {
            if (session.Path is null || session.FileName is null || !File.Exists(session.Path))
            {
                throw new InvalidOperationException("Upload a SQLite database before using the explorer.");
            }

            Touch(session);
            using var database = new SqliteDatabase(session.Path);
            return action(database, session.FileName);
        }
        finally
        {
            session.Gate.Release();
        }
    }

    private static void ValidateUpload(IFormFile file)
    {
        ArgumentNullException.ThrowIfNull(file);

        if (file.Length == 0)
        {
            throw new ArgumentException("Choose a non-empty database file.");
        }

        if (file.Length > MaximumUploadBytes)
        {
            throw new ArgumentException("Database files must be 10 MB or smaller.");
        }

        var extension = Path.GetExtension(file.FileName.Replace('\\', '/'));
        if (!SupportedExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Choose a .db, .sqlite, or .sqlite3 file.");
        }
    }

    private SessionDatabase GetOrCreateSession(string sessionId)
    {
        ValidateSessionId(sessionId);
        lock (sync)
        {
            var session = sessions.GetOrAdd(sessionId, static _ => new SessionDatabase());
            Touch(session);
            return session;
        }
    }

    private SessionDatabase GetSession(string sessionId)
    {
        CleanupExpiredSessions();
        ValidateSessionId(sessionId);
        lock (sync)
        {
            if (!sessions.TryGetValue(sessionId, out var session))
            {
                throw new InvalidOperationException("Upload a SQLite database before using the explorer.");
            }

            Touch(session);
            return session;
        }
    }

    public void CleanupExpiredSessions()
    {
        var expirationTicks = DateTime.UtcNow.Subtract(SessionRetention).Ticks;
        lock (sync)
        {
            foreach (var (sessionId, session) in sessions)
            {
                if (Interlocked.Read(ref session.LastAccessUtcTicks) >= expirationTicks || !session.Gate.Wait(0))
                {
                    continue;
                }

                var removed = false;
                try
                {
                    if (Interlocked.Read(ref session.LastAccessUtcTicks) < expirationTicks
                        && sessions.TryRemove(sessionId, out _))
                    {
                        removed = true;
                        if (session.Path is not null && File.Exists(session.Path))
                        {
                            File.Delete(session.Path);
                        }
                    }
                }
                finally
                {
                    session.Gate.Release();
                    if (removed)
                    {
                        session.Dispose();
                    }
                }
            }
        }
    }

    private static void Touch(SessionDatabase session) =>
        Interlocked.Exchange(ref session.LastAccessUtcTicks, DateTime.UtcNow.Ticks);

    private void DeleteExpiredOrphanedUploads()
    {
        var expiration = DateTime.UtcNow.Subtract(SessionRetention);
        foreach (var path in Directory.EnumerateFiles(uploadsDirectory))
        {
            if (File.GetLastWriteTimeUtc(path) < expiration)
            {
                File.Delete(path);
            }
        }
    }

    public void Dispose()
    {
        foreach (var session in sessions.Values)
        {
            session.Dispose();
        }
    }

    private static void ValidateSessionId(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            throw new InvalidOperationException("Upload a SQLite database before using the explorer.");
        }
    }

    private static string SafeFileName(string fileName)
    {
        var safeName = Path.GetFileName(fileName.Replace('\\', '/'));
        return string.IsNullOrWhiteSpace(safeName) ? "database.sqlite" : safeName;
    }

    private sealed class SessionDatabase : IDisposable
    {
        public readonly SemaphoreSlim Gate = new(1, 1);
        public string? Path;
        public string? FileName;
        public long LastAccessUtcTicks;

        public void Dispose() => Gate.Dispose();
    }
}

public sealed record ExplorerOverview(string FileName, DatabaseOverview Database);

public sealed partial class UploadCleanupService(ExplorerStore store, ILogger<UploadCleanupService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(5));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                store.CleanupExpiredSessions();
            }
            catch (IOException exception)
            {
                LogCleanupFailed(logger, exception);
            }
        }
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Error, Message = "Could not remove expired SQLite uploads.")]
    private static partial void LogCleanupFailed(ILogger logger, Exception exception);
}
