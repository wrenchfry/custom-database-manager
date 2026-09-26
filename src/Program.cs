using CodecraftersSqlite;

if (args.Length >= 2)
{
    using var database = new SqliteDatabase(args[0]);
    var command = string.Join(' ', args.Skip(1)).Trim();
    if (command == ".dbinfo") Console.WriteLine($"database page size: {database.PageSize}\nnumber of tables: {database.Tables.Count}");
    else if (command == ".tables") Console.WriteLine(string.Join(' ', database.Tables.Select(x => x.Name)));
    else if (command == ".schema") foreach (var table in database.Tables) Console.WriteLine(table.Sql);
    else
    {
        var query = SelectQuery.Parse(command); var table = database.GetTable(query.Table);
        var rows = database.ReadRows(table).Where(x => query.Filter is null || query.Filter.Matches(x, table.Columns));
        if (query.IsCount) Console.WriteLine(rows.Count());
        else foreach (var row in rows) Console.WriteLine(string.Join("|", (query.Columns is ["*"] ? table.Columns : query.Columns).Select(x => row.ValueFor(x, table.Columns))));
    }
    return;
}

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton<NotebookStore>();
var app = builder.Build();
app.UseDefaultFiles(); app.UseStaticFiles();
app.MapGet("/api/notes", (NotebookStore s) => s.Notes());
app.MapPost("/api/notes", (NoteInput n, NotebookStore s) => Results.Created("", s.Create(n)));
app.MapPut("/api/notes/{id:int}", (int id, NoteInput n, NotebookStore s) => s.Update(id, n) ? Results.NoContent() : Results.NotFound());
app.MapDelete("/api/notes/{id:int}", (int id, NotebookStore s) => s.Delete(id) ? Results.NoContent() : Results.NotFound());
app.MapGet("/api/cards", (NotebookStore s) => s.Due());
app.MapPost("/api/cards", (CardInput c, NotebookStore s) => Results.Created("", s.CreateCard(c)));
app.MapPost("/api/cards/{id:int}/review", (int id, ReviewInput r, NotebookStore s) => s.Review(id, r.Rating) ? Results.NoContent() : Results.NotFound());
app.MapGet("/api/inspect", (NotebookStore s) => s.Inspect());
app.Run();
