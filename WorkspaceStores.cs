using System.Text.Json;
using System.IO;

namespace WindowsMssqlStudio;

public sealed class QueryDocumentState
{
    public string Name { get; set; } = "SQLQuery1.sql";
    public string Text { get; set; } = "";
}

public sealed class QueryHistoryEntry
{
    public DateTimeOffset StartedAt { get; set; }
    public string Sql { get; set; } = "";
    public string Server { get; set; } = "";
    public string Database { get; set; } = "";
    public long DurationMs { get; set; }
    public int ResultSets { get; set; }
    public int Rows { get; set; }
    public int AffectedRows { get; set; }
    public bool Succeeded { get; set; }
    public string Error { get; set; } = "";
}

public static class WorkspaceStores
{
    private static readonly string Root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MkaSqlStudio");
    private static readonly string SessionPath = Path.Combine(Root, "session.json");
    private static readonly string HistoryPath = Path.Combine(Root, "query-history.json");
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static IReadOnlyList<QueryDocumentState> LoadSession()
    {
        try
        {
            if (!File.Exists(SessionPath)) return [];
            return JsonSerializer.Deserialize<List<QueryDocumentState>>(File.ReadAllText(SessionPath)) ?? [];
        }
        catch { return []; }
    }

    public static void SaveSession(IEnumerable<QueryDocumentState> documents)
    {
        Directory.CreateDirectory(Root);
        File.WriteAllText(SessionPath, JsonSerializer.Serialize(documents.ToList(), JsonOptions));
    }

    public static IReadOnlyList<QueryHistoryEntry> LoadHistory()
    {
        try
        {
            if (!File.Exists(HistoryPath)) return [];
            return JsonSerializer.Deserialize<List<QueryHistoryEntry>>(File.ReadAllText(HistoryPath)) ?? [];
        }
        catch { return []; }
    }

    public static void AddHistory(QueryHistoryEntry entry)
    {
        Directory.CreateDirectory(Root);
        var history = LoadHistory().ToList();
        history.Insert(0, entry);
        File.WriteAllText(HistoryPath, JsonSerializer.Serialize(history.Take(200).ToList(), JsonOptions));
    }
}

