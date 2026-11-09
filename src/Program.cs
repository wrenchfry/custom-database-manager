using SqliteUnderTheHood;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;

if (args.Length >= 2)
{
    try
    {
        RunInspector(args);
    }
    catch (Exception exception) when (exception is IOException or ArgumentException or NotSupportedException)
    {
        Console.Error.WriteLine(exception.Message);
        Environment.ExitCode = 1;
    }

    return;
}
var builder = WebApplication.CreateBuilder(args);
var port = Environment.GetEnvironmentVariable("PORT");
if (!string.IsNullOrWhiteSpace(port))
{
    builder.WebHost.UseUrls($"http://0.0.0.0:{port}");
}

builder.Services.AddDistributedMemoryCache();
builder.Services.AddSession(options =>
{
    options.IdleTimeout = TimeSpan.FromMinutes(30);
    options.Cookie.HttpOnly = true;
    options.Cookie.IsEssential = true;
    options.Cookie.SameSite = SameSiteMode.Strict;
    options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
});
builder.Services.AddSingleton<ExplorerStore>();
builder.Services.AddHostedService<UploadCleanupService>();
builder.Services.Configure<FormOptions>(options => options.MultipartBodyLengthLimit = ExplorerStore.MaximumUploadBytes + 64 * 1024);

var app = builder.Build();
app.UseExceptionHandler(exceptionApp => exceptionApp.Run(async context =>
{
    context.Response.StatusCode = StatusCodes.Status500InternalServerError;
    await Results.Problem("An unexpected server error occurred.", statusCode: 500).ExecuteAsync(context);
}));
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseSession();

app.MapPost("/api/database", async ([FromForm] IFormFile file, HttpContext context, ExplorerStore store, CancellationToken cancellationToken) =>
{
    try
    {
        context.Session.SetString(ExplorerStore.SessionInitializedKey, "1");
        return Results.Ok(await store.LoadAsync(context.Session.Id, file, cancellationToken));
    }
    catch (ArgumentException exception)
    {
        return Results.BadRequest(new { error = exception.Message });
    }
    catch (InvalidDataException exception)
    {
        return Results.BadRequest(new { error = exception.Message });
    }
}).DisableAntiforgery();

app.MapGet("/api/database", (HttpContext context, ExplorerStore store) =>
    Execute(() => store.Overview(context.Session.Id)));
app.MapPost("/api/query", (QueryRequest request, HttpContext context, ExplorerStore store) =>
    Execute(() => store.RunQuery(context.Session.Id, request.Sql)));
app.MapGet("/api/pages/{pageNumber:int}", (int pageNumber, HttpContext context, ExplorerStore store) =>
    Execute(() => store.InspectPage(context.Session.Id, pageNumber)));
app.MapGet("/api/tables/{tableName}/tree", (string tableName, HttpContext context, ExplorerStore store) =>
    Execute(() => store.GetTableTree(context.Session.Id, tableName)));

app.Run();

static IResult Execute<T>(Func<T> action)
{
    try
    {
        return Results.Ok(action());
    }
    catch (InvalidOperationException exception)
    {
        return Results.BadRequest(new { error = exception.Message });
    }
    catch (ArgumentException exception)
    {
        return Results.BadRequest(new { error = exception.Message });
    }
    catch (InvalidDataException exception)
    {
        return Results.BadRequest(new { error = exception.Message });
    }
    catch (NotSupportedException exception)
    {
        return Results.BadRequest(new { error = exception.Message });
    }
}
static void RunInspector(string[] arguments)
{
    using var database = new SqliteDatabase(arguments[0]);
    var command = string.Join(' ', arguments.Skip(1)).Trim();

    switch (command)
    {
        case ".dbinfo":
            var overview = database.Overview();
            Console.WriteLine($"database page size: {overview.PageSize}\nnumber of tables: {overview.Tables.Count}");
            break;
        case ".tables":
            Console.WriteLine(string.Join(' ', database.Tables.Select(table => table.Name)));
            break;
        case ".schema":
            foreach (var table in database.Tables)
            {
                Console.WriteLine(table.Sql);
            }

            break;
        default:
            var result = database.RunQuery(command);
            foreach (var row in result.Rows)
            {
                Console.WriteLine(string.Join("|", row));
            }

            if (result.Truncated)
            {
                Console.Error.WriteLine("Results were limited to 500 rows.");
            }

            break;
    }
}
