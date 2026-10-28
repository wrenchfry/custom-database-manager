using CodecraftersSqlite;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.FileProviders;
using Xunit;

namespace CodecraftersSqlite.Tests;

public sealed class ExplorerStoreTests
{
    [Fact]
    public async Task LoadAsyncAcceptsASqliteDatabase()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        await using var source = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "sample.db"));
        var file = new FormFile(source, 0, source.Length, "file", "sample.db");
        var store = new ExplorerStore(new TestHostEnvironment(directory));

        try
        {
            var overview = await store.LoadAsync(file, CancellationToken.None);

            Assert.Equal("sample.db", overview.FileName);
            Assert.Contains(overview.Database.Tables, table => table.Name == "apples");
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    private sealed class TestHostEnvironment(string contentRootPath) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "Tests";
        public string ContentRootPath { get; set; } = contentRootPath;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
