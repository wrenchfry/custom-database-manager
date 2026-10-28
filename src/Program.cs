using CodecraftersSqlite;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;

if (args.Length >= 2)
{
    RunInspector(args);
    return;
}
var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton<ExplorerStore>();
builder.Services.Configure<FormOptions>(options => options.MultipartBodyLengthLimit = 10 * 1024 * 1024);

var app = builder.Build();
app.UseExceptionHandler(exceptionApp => exceptionApp.Run(async context =>
{
    context.Response.StatusCode = StatusCodes.Status500InternalServerError;
    await Results.Problem("An unexpected server error occurred.", statusCode: 500).ExecuteAsync(context);
}));
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapPost("/api/database", async ([FromForm] IFormFile file, ExplorerStore store, CancellationToken cancellationToken) =>
{
    try
    {
        return Results.Ok(await store.LoadAsync(file, cancellationToken));
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

app.MapGet("/api/database", (ExplorerStore store) => Execute(() => store.Overview()));
app.MapPost("/api/query", (QueryRequest request, ExplorerStore store) =>
    Execute(() => store.RunQuery(request.Sql)));
app.MapGet("/api/pages/{pageNumber:int}", (int pageNumber, ExplorerStore store) =>
    Execute(() => store.InspectPage(pageNumber)));
app.MapGet("/api/tables/{tableName}/tree", (string tableName, ExplorerStore store) =>
    Execute(() => store.GetTableTree(tableName)));

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

