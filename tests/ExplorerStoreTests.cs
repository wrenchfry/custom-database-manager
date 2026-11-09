using SqliteUnderTheHood;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Data.Sqlite;
using Xunit;

namespace SqliteUnderTheHood.Tests;

public sealed class ExplorerStoreTests
{
    [Fact]
    public async Task LoadsSeparateDatabasesForSeparateSessions()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var firstPath = CreateDatabase("first-session");
        var secondPath = CreateDatabase("second-session");
        var store = new ExplorerStore(new TestHostEnvironment(directory));

        try
        {
            ExplorerOverview firstOverview;
            await using (var firstSource = File.OpenRead(firstPath))
            {
                var firstFile = new FormFile(firstSource, 0, firstSource.Length, "file", "first.sqlite");
                firstOverview = await store.LoadAsync("session-one", firstFile, CancellationToken.None);
            }

            ExplorerOverview secondOverview;
            await using (var secondSource = File.OpenRead(secondPath))
            {
                var secondFile = new FormFile(secondSource, 0, secondSource.Length, "file", "second.sqlite");
                secondOverview = await store.LoadAsync("session-two", secondFile, CancellationToken.None);
            }

            Assert.Equal("first.sqlite", firstOverview.FileName);
            Assert.Equal("second.sqlite", secondOverview.FileName);
            Assert.Equal("first-session", store.RunQuery("session-one", "SELECT name FROM apples").Rows.Single().Single());
            Assert.Equal("second-session", store.RunQuery("session-two", "SELECT name FROM apples").Rows.Single().Single());
        }
        finally
        {
            File.Delete(firstPath);
            File.Delete(secondPath);
            Directory.Delete(directory, true);
            var applicationId = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(directory)));
            var uploadsPath = Path.Combine(Path.GetTempPath(), "sqlite-under-the-hood", applicationId);
            if (Directory.Exists(uploadsPath))
            {
                Directory.Delete(uploadsPath, true);
            }
        }
    }

    [Fact]
    public async Task RejectsInvalidDatabaseFilesWithoutReplacingExistingSessionData()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var validPath = CreateDatabase("retained-data");
        var invalidPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.sqlite");
        await File.WriteAllTextAsync(invalidPath, "not a database");
        var store = new ExplorerStore(new TestHostEnvironment(directory));

        try
        {
            await using (var validSource = File.OpenRead(validPath))
            {
                var validFile = new FormFile(validSource, 0, validSource.Length, "file", "valid.sqlite");
                await store.LoadAsync("session", validFile, CancellationToken.None);
            }

            await using (var invalidSource = File.OpenRead(invalidPath))
            {
                var invalidFile = new FormFile(invalidSource, 0, invalidSource.Length, "file", "invalid.sqlite");
                await Assert.ThrowsAsync<InvalidDataException>(() => store.LoadAsync("session", invalidFile, CancellationToken.None));
            }

            Assert.Equal("retained-data", store.RunQuery("session", "SELECT name FROM apples").Rows.Single().Single());
        }
        finally
        {
            File.Delete(validPath);
            File.Delete(invalidPath);
            Directory.Delete(directory, true);
            var applicationId = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(directory)));
            var uploadsPath = Path.Combine(Path.GetTempPath(), "sqlite-under-the-hood", applicationId);
            if (Directory.Exists(uploadsPath))
            {
                Directory.Delete(uploadsPath, true);
            }
        }
    }

    private static string CreateDatabase(string value)
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.sqlite");
        using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE apples (id INTEGER PRIMARY KEY, name TEXT); INSERT INTO apples (name) VALUES ($name);";
        command.Parameters.AddWithValue("$name", value);
        command.ExecuteNonQuery();
        return path;
    }

    private sealed class TestHostEnvironment(string contentRootPath) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "Tests";
        public string ContentRootPath { get; set; } = contentRootPath;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
