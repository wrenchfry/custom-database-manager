using Microsoft.Data.Sqlite;

namespace CodecraftersSqlite;

public sealed class NotebookStore
{
    private readonly string path;
    public NotebookStore(IHostEnvironment env)
    {
        var folder = Path.Combine(env.ContentRootPath, "data"); Directory.CreateDirectory(folder); path = Path.Combine(folder, "notebook.db");
        using var db = Open();
        Run(db, "CREATE TABLE IF NOT EXISTS notes (id INTEGER PRIMARY KEY, title TEXT NOT NULL, content TEXT NOT NULL, tags TEXT NOT NULL, updated TEXT NOT NULL)");
        Run(db, "CREATE TABLE IF NOT EXISTS cards (id INTEGER PRIMARY KEY, note_id INTEGER NOT NULL, front TEXT NOT NULL, back TEXT NOT NULL, interval_days INTEGER NOT NULL, ease REAL NOT NULL, due TEXT NOT NULL)");
        if (Count(db, "notes") == 0) Create(new("Welcome to Research Notebook", "A private study workspace for notes and active recall.\n\nCreate a note, tag it, then turn an idea into a flashcard.", "welcome,study"));
    }
    public IEnumerable<Note> Notes() { using var db=Open(); using var c=db.CreateCommand(); c.CommandText="SELECT id,title,content,tags,updated FROM notes ORDER BY updated DESC"; using var r=c.ExecuteReader(); while(r.Read()) yield return new(r.GetInt32(0),r.GetString(1),r.GetString(2),r.GetString(3),r.GetString(4)); }
    public Note Create(NoteInput n) { using var db=Open(); var now=DateTimeOffset.UtcNow.ToString("O"); using var c=db.CreateCommand(); c.CommandText="INSERT INTO notes(title,content,tags,updated) VALUES($t,$c,$g,$u); SELECT last_insert_rowid();"; c.Parameters.AddWithValue("$t",Text(n.Title,"Untitled note"));c.Parameters.AddWithValue("$c",n.Content??"");c.Parameters.AddWithValue("$g",n.Tags??"");c.Parameters.AddWithValue("$u",now); return new(Convert.ToInt32((long)c.ExecuteScalar()!),Text(n.Title,"Untitled note"),n.Content??"",n.Tags??"",now); }
    public bool Update(int id,NoteInput n) { using var db=Open(); return Run(db,"UPDATE notes SET title=$t,content=$c,tags=$g,updated=$u WHERE id=$id",("$id",id),("$t",Text(n.Title,"Untitled note")),("$c",n.Content??""),("$g",n.Tags??""),("$u",DateTimeOffset.UtcNow.ToString("O")))==1; }
    public bool Delete(int id) { using var db=Open(); Run(db,"DELETE FROM cards WHERE note_id=$id",("$id",id)); return Run(db,"DELETE FROM notes WHERE id=$id",("$id",id))==1; }
    public IEnumerable<Card> Due() { using var db=Open(); using var c=db.CreateCommand(); c.CommandText="SELECT id,note_id,front,back,interval_days,ease,due FROM cards WHERE due<=$now ORDER BY due";c.Parameters.AddWithValue("$now",DateTimeOffset.UtcNow.ToString("O"));using var r=c.ExecuteReader();while(r.Read())yield return new(r.GetInt32(0),r.GetInt32(1),r.GetString(2),r.GetString(3),r.GetInt32(4),r.GetDouble(5),r.GetString(6)); }
    public Card CreateCard(CardInput x) { using var db=Open();var due=DateTimeOffset.UtcNow.ToString("O");using var c=db.CreateCommand();c.CommandText="INSERT INTO cards(note_id,front,back,interval_days,ease,due) VALUES($n,$f,$b,0,2.5,$d);SELECT last_insert_rowid();";c.Parameters.AddWithValue("$n",x.NoteId);c.Parameters.AddWithValue("$f",Text(x.Front,"Question"));c.Parameters.AddWithValue("$b",Text(x.Back,"Answer"));c.Parameters.AddWithValue("$d",due);return new(Convert.ToInt32((long)c.ExecuteScalar()!),x.NoteId,Text(x.Front,"Question"),Text(x.Back,"Answer"),0,2.5,due); }
    public bool Review(int id,int rating) { using var db=Open();using var c=db.CreateCommand();c.CommandText="SELECT interval_days,ease FROM cards WHERE id=$id";c.Parameters.AddWithValue("$id",id);using var r=c.ExecuteReader();if(!r.Read())return false;var days=r.GetInt32(0);var ease=r.GetDouble(1);r.Close();rating=Math.Clamp(rating,0,3);days=rating<2?1:Math.Max(1,(int)Math.Round(Math.Max(1,days)*ease));ease=Math.Max(1.3,ease+(rating-2)*.15);Run(db,"UPDATE cards SET interval_days=$i,ease=$e,due=$d WHERE id=$id",("$i",days),("$e",ease),("$d",DateTimeOffset.UtcNow.AddDays(days).ToString("O")),("$id",id));return true; }
    public object Inspect() { using var d=new SqliteDatabase(path);return new {d.PageSize,tables=d.Tables.Select(t=>new {t.Name,t.RootPage,t.Columns,rows=d.ReadRows(t).Count()})}; }
    private SqliteConnection Open(){var d=new SqliteConnection($"Data Source={path}");d.Open();return d;} private static string Text(string? x,string fallback)=>string.IsNullOrWhiteSpace(x)?fallback:x.Trim();private static long Count(SqliteConnection d,string t){using var c=d.CreateCommand();c.CommandText="SELECT count(*) FROM "+t;return(long)c.ExecuteScalar()!;}private static int Run(SqliteConnection d,string q,params (string,object)[] p){using var c=d.CreateCommand();c.CommandText=q;foreach(var x in p)c.Parameters.AddWithValue(x.Item1,x.Item2);return c.ExecuteNonQuery();}
}
public sealed record Note(int Id,string Title,string Content,string Tags,string Updated);
public sealed record NoteInput(string? Title,string? Content,string? Tags);
public sealed record Card(int Id,int NoteId,string Front,string Back,int IntervalDays,double Ease,string Due);
public sealed record CardInput(int NoteId,string? Front,string? Back);
public sealed record ReviewInput(int Rating);
