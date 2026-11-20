using System.Data;
using System.Diagnostics;
using System.Collections.ObjectModel;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using System.Reflection;
using Microsoft.Data.SqlClient;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Highlighting.Xshd;
using System.Xml;

namespace WindowsMssqlStudio;

public partial class MainWindow
{
    private static string AppVersion => Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "0.3.0";
    private SqlConnection? connection;
    private CancellationTokenSource? queryCancellation;
    private bool suppressEditorChanges;
    private bool insertingAutomaticAlias;
    private TabItem? activeQueryTab;
    private int queryNumber = 1;
    private readonly HashSet<string> completionSymbols = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> tableNames = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<string>> tableColumns = new(StringComparer.OrdinalIgnoreCase);
    private DataView? resultView;
    private readonly Dictionary<string, string> resultFilters = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<QueryHistoryEntry> queryHistory = [];
    private readonly string[] sqlKeywords = ["SELECT", "FROM", "WHERE", "JOIN", "INNER JOIN", "LEFT JOIN", "RIGHT JOIN", "GROUP BY", "ORDER BY", "HAVING", "INSERT INTO", "VALUES", "UPDATE", "SET", "DELETE FROM", "CREATE", "ALTER", "DROP", "EXEC", "DECLARE", "AS", "AND", "OR", "NOT", "NULL", "IS NULL", "TOP", "DISTINCT", "COUNT", "SUM", "AVG", "MIN", "MAX", "CASE", "WHEN", "THEN", "ELSE", "END", "BEGIN", "COMMIT", "ROLLBACK", "WITH"];
    private sealed record CompletionItem(string Display, string InsertText);
    private sealed record ExplorerNode(string Kind, string Schema = "", string Name = "");

    public MainWindow()
    {
        InitializeComponent();
        SchemaTree.AddHandler(TreeViewItem.ExpandedEvent, new RoutedEventHandler(SchemaTree_Expanded));
        SchemaTree.AddHandler(FrameworkElement.ContextMenuOpeningEvent, new ContextMenuEventHandler(SchemaTree_ContextMenuOpening));
        SchemaTree.MouseDoubleClick += SchemaTree_MouseDoubleClick;
        IntelliSensePopup.PlacementTarget = SqlEditor;
        try { ConfigureSqlHighlighting(); }
        catch { SqlEditor.SyntaxHighlighting = HighlightingManager.Instance.GetDefinition("C#"); }
        VersionText.Text = $"v{AppVersion} · SQL Server çalışma alanı";
        HeaderVersionText.Text = $"v{AppVersion}";
        UpdateButton.Content = $"v{AppVersion}  Güncelle";
        FooterVersionText.Text = $"MKA SQL Studio · Windows · v{AppVersion}";
        ResultsGrid.RowHeight = 28;
        ResultsGrid.EnableRowVirtualization = true;
        ResultsGrid.EnableColumnVirtualization = true;
        ResultsGrid.SelectionUnit = DataGridSelectionUnit.FullRow;
        ResultsGrid.ClipboardCopyMode = DataGridClipboardCopyMode.IncludeHeader;
        SqlEditor.TextArea.Caret.PositionChanged += (_, _) => UpdateCursorStatus();
        Closing += MainWindow_Closing;
        queryHistory.AddRange(WorkspaceStores.LoadHistory());
        Loaded += MainWindow_Loaded;
    }

    private void ConfigureSqlHighlighting()
    {
        const string definition = """
            <SyntaxDefinition name="MSSQL Studio" extensions=".sql" xmlns="http://icsharpcode.net/sharpdevelop/syntaxdefinition/2008">
              <Color name="Keyword" foreground="#0759C7" fontWeight="bold" />
              <Color name="Comment" foreground="#4F8A10" />
              <Color name="String" foreground="#A31515" />
              <Color name="Variable" foreground="#C000C0" />
              <Color name="Number" foreground="#098658" />
              <RuleSet ignoreCase="true">
                <Span color="Comment" begin="--" end="$" />
                <Span color="Comment" multiline="true" begin="/\\*" end="\\*/" />
                <Span color="String" begin="'" end="'" />
                <Rule color="Variable">\\@[A-Za-z_][A-Za-z0-9_]*</Rule>
                <Rule color="Number">\\b[0-9]+(\\.[0-9]+)?\\b</Rule>
                <Keywords color="Keyword">
                  <Word>SELECT</Word><Word>FROM</Word><Word>WHERE</Word><Word>JOIN</Word><Word>INNER</Word><Word>LEFT</Word><Word>RIGHT</Word><Word>FULL</Word><Word>OUTER</Word><Word>ON</Word><Word>AS</Word><Word>AND</Word><Word>OR</Word><Word>NOT</Word><Word>NULL</Word><Word>IS</Word><Word>IN</Word><Word>LIKE</Word><Word>BETWEEN</Word><Word>GROUP</Word><Word>BY</Word><Word>ORDER</Word><Word>HAVING</Word><Word>TOP</Word><Word>DISTINCT</Word><Word>INSERT</Word><Word>INTO</Word><Word>VALUES</Word><Word>UPDATE</Word><Word>SET</Word><Word>DELETE</Word><Word>MERGE</Word><Word>CREATE</Word><Word>ALTER</Word><Word>DROP</Word><Word>EXEC</Word><Word>DECLARE</Word><Word>BEGIN</Word><Word>END</Word><Word>CASE</Word><Word>WHEN</Word><Word>THEN</Word><Word>ELSE</Word><Word>WITH</Word><Word>UNION</Word><Word>ALL</Word><Word>COUNT</Word><Word>SUM</Word><Word>AVG</Word><Word>MIN</Word><Word>MAX</Word>
                </Keywords>
              </RuleSet>
            </SyntaxDefinition>
            """;
        using var reader = XmlReader.Create(new StringReader(definition));
        SqlEditor.SyntaxHighlighting = HighlightingLoader.Load(reader, HighlightingManager.Instance);
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        Loaded -= MainWindow_Loaded;
        Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () =>
        {
            RestoreSession();
            SqlEditor.Focus();
        });
    }

    private void RestoreSession()
    {
        var documents = WorkspaceStores.LoadSession();
        if (documents.Count == 0)
        {
            SetEditorText("SELECT TOP (100) name, type_desc\nFROM sys.objects\nORDER BY name;");
            activeQueryTab = QueryTabs.SelectedItem as TabItem;
            if (activeQueryTab is not null) { activeQueryTab.Tag = GetEditorText(); SetTabHeader(activeQueryTab, "SQLQuery1.sql"); }
            UpdateCursorStatus();
            return;
        }

        QueryTabs.Items.Clear();
        queryNumber = 0;
        foreach (var document in documents)
        {
            queryNumber++;
            var tab = new TabItem { Tag = document.Text };
            SetTabHeader(tab, string.IsNullOrWhiteSpace(document.Name) ? $"SQLQuery{queryNumber}.sql" : document.Name);
            QueryTabs.Items.Add(tab);
        }
        QueryTabs.SelectedIndex = 0;
        activeQueryTab = QueryTabs.SelectedItem as TabItem;
        SetEditorText(activeQueryTab?.Tag as string ?? "");
        UpdateCursorStatus();
    }

    private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        SaveActiveQuery();
        WorkspaceStores.SaveSession(QueryTabs.Items.OfType<TabItem>().Select((tab, index) => new QueryDocumentState
        {
            Name = GetTabName(tab, index),
            Text = tab.Tag as string ?? ""
        }));
    }

    private static string GetTabName(TabItem tab, int index) => $"SQLQuery{index + 1}.sql";
    private void SaveSession_Click(object sender, RoutedEventArgs e) { MainWindow_Closing(this, new System.ComponentModel.CancelEventArgs()); StatusText.Text = "Oturum kaydedildi"; }
    private void UpdateCursorStatus() => CursorText.Text = $"Satır {SqlEditor.TextArea.Caret.Line}, Sütun {SqlEditor.TextArea.Caret.Column}";
    private void ShowHistory_Click(object sender, RoutedEventArgs e)
    {
        var recent = queryHistory.Take(12).Select(item => $"{item.StartedAt.LocalDateTime:dd.MM.yyyy HH:mm} · {(item.Succeeded ? "✓" : "✕")} · {item.DurationMs} ms · {item.Database}\n{item.Sql.Replace("\r", "").Replace("\n", " ").Trim()}");
        MessageBox.Show(this, recent.Any() ? string.Join("\n\n", recent) : "Henüz sorgu geçmişi yok.", "Sorgu geçmişi");
    }

    private async void Connect_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ConnectionDialog { Owner = this };
        if (dialog.ShowDialog() != true) return;

        try
        {
            connection?.Dispose();
            connection = new SqlConnection(dialog.ConnectionString);
            await connection.OpenAsync();
            if (dialog.SelectedProfile is not null) ConnectionProfileStore.Save(dialog.SelectedProfile);
            ConnectionLabel.Text = connection.DataSource + $"\n{(dialog.SelectedDatabase ?? "Sunucu")}";
            TopConnectionText.Text = $"  {connection.DataSource} · {(dialog.SelectedDatabase ?? "Sunucu")}";
            StatusText.Text = "Bağlandı";
            MessagesBox.Text = "Bağlantı başarılı.";
            if (dialog.SelectedDatabase is not null) await LoadSchemaAsync();
        }
        catch (Exception ex)
        {
            connection?.Dispose();
            connection = null;
            ShowError("Bağlantı kurulamadı", ex);
        }
    }

    private async Task LoadSchemaAsync()
    {
        if (connection is null) return;
        SchemaTree.Items.Clear();
        completionSymbols.Clear();
        tableNames.Clear();
        tableColumns.Clear();
        completionSymbols.Add(connection.Database);
        await LoadTableColumnsAsync();
        var database = new TreeViewItem { Header = $"{connection.Database}  (Database)", Tag = new ExplorerNode("Database"), IsExpanded = true };
        AddExpandable(database, "Tables", "Tables");
        AddExpandable(database, "Views", "Views");
        var programmable = AddExpandable(database, "Programmability", "Programmability");
        AddExpandable(programmable, "Stored Procedures", "Procedures");
        AddExpandable(programmable, "Functions", "Functions");
        AddExpandable(programmable, "Triggers", "Triggers");
        AddExpandable(database, "Synonyms", "Synonyms");
        AddExpandable(database, "Security", "Security");
        SchemaTree.Items.Add(database);
    }

    private async Task LoadTableColumnsAsync()
    {
        if (connection is null) return;
        await using var command = new SqlCommand("SELECT s.name, o.name, c.name FROM sys.columns c JOIN sys.objects o ON o.object_id=c.object_id JOIN sys.schemas s ON s.schema_id=o.schema_id WHERE o.is_ms_shipped=0 ORDER BY s.name,o.name,c.column_id", connection);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var schema = reader.GetString(0);
            var table = reader.GetString(1);
            var column = reader.GetString(2);
            tableColumns.TryAdd(table, []);
            tableColumns.TryAdd($"{schema}.{table}", []);
            tableColumns[table].Add(column);
            tableColumns[$"{schema}.{table}"].Add(column);
            completionSymbols.Add(column);
            tableNames.Add(table);
        }
    }

    private static TreeViewItem AddExpandable(TreeViewItem parent, string header, string kind, string schema = "", string name = "")
    {
        var item = new TreeViewItem { Header = header, Tag = new ExplorerNode(kind, schema, name) };
        item.Items.Add(new TreeViewItem { Header = "" });
        parent.Items.Add(item);
        return item;
    }

    private async void SchemaTree_Expanded(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is not TreeViewItem item || item.Tag is not ExplorerNode node || item.Items.Count != 1 || item.Items[0] is not TreeViewItem placeholder || placeholder.Header?.ToString() != "") return;
        item.Items.Clear();
        try { await LoadExplorerChildrenAsync(item, node); }
        catch (Exception ex) { item.Items.Add(new TreeViewItem { Header = $"Hata: {ex.Message}" }); }
    }

    private async Task LoadExplorerChildrenAsync(TreeViewItem parent, ExplorerNode node)
    {
        switch (node.Kind)
        {
            case "Tables": await AddObjectsAsync(parent, "U", "Table"); break;
            case "Views": await AddObjectsAsync(parent, "V", "View"); break;
            case "Procedures": await AddObjectsAsync(parent, "P", "Procedure"); break;
            case "Functions": await AddFunctionsAsync(parent); break;
            case "Triggers": await AddObjectsAsync(parent, "TR", "Trigger"); break;
            case "Synonyms": await AddObjectsAsync(parent, "SN", "Synonym"); break;
            case "Table": AddExpandable(parent, "Columns", "TableColumns", node.Schema, node.Name); AddExpandable(parent, "Indexes", "TableIndexes", node.Schema, node.Name); AddExpandable(parent, "Keys", "TableKeys", node.Schema, node.Name); AddExpandable(parent, "Constraints", "TableConstraints", node.Schema, node.Name); AddExpandable(parent, "Triggers", "TableTriggers", node.Schema, node.Name); break;
            case "View": AddExpandable(parent, "Columns", "ViewColumns", node.Schema, node.Name); break;
            case "Procedure": AddExpandable(parent, "Parameters", "ProcedureParameters", node.Schema, node.Name); AddExpandable(parent, "Definition", "ProcedureDefinition", node.Schema, node.Name); break;
            case "Function": AddExpandable(parent, "Parameters", "FunctionParameters", node.Schema, node.Name); AddExpandable(parent, "Return columns", "FunctionColumns", node.Schema, node.Name); AddExpandable(parent, "Definition", "FunctionDefinition", node.Schema, node.Name); break;
            case "TableColumns": await AddRowsAsync(parent, "SELECT c.name, TYPE_NAME(c.user_type_id) + CASE WHEN c.max_length = -1 THEN '(max)' ELSE '' END FROM sys.columns c JOIN sys.objects o ON o.object_id=c.object_id JOIN sys.schemas s ON s.schema_id=o.schema_id WHERE s.name=@schema AND o.name=@name ORDER BY c.column_id", node); break;
            case "ViewColumns": await AddRowsAsync(parent, "SELECT c.name, TYPE_NAME(c.user_type_id) FROM sys.columns c JOIN sys.objects o ON o.object_id=c.object_id JOIN sys.schemas s ON s.schema_id=o.schema_id WHERE s.name=@schema AND o.name=@name ORDER BY c.column_id", node); break;
            case "TableIndexes": await AddRowsAsync(parent, "SELECT i.name, i.type_desc FROM sys.indexes i JOIN sys.objects o ON o.object_id=i.object_id JOIN sys.schemas s ON s.schema_id=o.schema_id WHERE s.name=@schema AND o.name=@name AND i.name IS NOT NULL ORDER BY i.name", node); break;
            case "TableKeys": await AddRowsAsync(parent, "SELECT kc.name, kc.type_desc FROM sys.key_constraints kc JOIN sys.objects o ON o.object_id=kc.parent_object_id JOIN sys.schemas s ON s.schema_id=o.schema_id WHERE s.name=@schema AND o.name=@name ORDER BY kc.name", node); break;
            case "TableConstraints": await AddRowsAsync(parent, "SELECT cc.name, cc.type_desc FROM sys.objects cc JOIN sys.objects o ON o.object_id=cc.parent_object_id JOIN sys.schemas s ON s.schema_id=o.schema_id WHERE s.name=@schema AND o.name=@name AND cc.type IN ('C','D','F') ORDER BY cc.name", node); break;
            case "TableTriggers": await AddRowsAsync(parent, "SELECT t.name, t.type_desc FROM sys.triggers t JOIN sys.objects o ON o.object_id=t.parent_id JOIN sys.schemas s ON s.schema_id=o.schema_id WHERE s.name=@schema AND o.name=@name ORDER BY t.name", node); break;
            case "ProcedureParameters": case "FunctionParameters": await AddRowsAsync(parent, "SELECT p.name, TYPE_NAME(p.user_type_id) FROM sys.parameters p JOIN sys.objects o ON o.object_id=p.object_id JOIN sys.schemas s ON s.schema_id=o.schema_id WHERE s.name=@schema AND o.name=@name ORDER BY p.parameter_id", node); break;
            case "ProcedureDefinition": case "FunctionDefinition": await AddDefinitionAsync(parent, node); break;
            case "FunctionColumns": await AddRowsAsync(parent, "SELECT c.name, TYPE_NAME(c.user_type_id) FROM sys.columns c JOIN sys.objects o ON o.object_id=c.object_id JOIN sys.schemas s ON s.schema_id=o.schema_id WHERE s.name=@schema AND o.name=@name ORDER BY c.column_id", node); break;
            case "Security": await AddRowsAsync(parent, "SELECT name, type_desc FROM sys.database_principals WHERE principal_id > 4 ORDER BY type_desc, name", node); break;
        }
    }

    private async Task AddObjectsAsync(TreeViewItem parent, string type, string kind)
    {
        await using var command = new SqlCommand("SELECT s.name, o.name FROM sys.objects o JOIN sys.schemas s ON s.schema_id=o.schema_id WHERE o.is_ms_shipped=0 AND o.type=@type ORDER BY s.name,o.name", connection);
        command.Parameters.AddWithValue("@type", type); await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            completionSymbols.Add(reader.GetString(0));
            completionSymbols.Add(reader.GetString(1));
            if (type == "U") tableNames.Add(reader.GetString(1));
            AddExpandable(parent, reader.GetString(0) + "." + reader.GetString(1), kind, reader.GetString(0), reader.GetString(1));
        }
    }

    private async Task AddFunctionsAsync(TreeViewItem parent)
    {
        await using var command = new SqlCommand("SELECT s.name, o.name FROM sys.objects o JOIN sys.schemas s ON s.schema_id=o.schema_id WHERE o.is_ms_shipped=0 AND o.type IN ('FN','IF','TF','FS','FT') ORDER BY s.name,o.name", connection);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            completionSymbols.Add(reader.GetString(0));
            completionSymbols.Add(reader.GetString(1));
            AddExpandable(parent, reader.GetString(0) + "." + reader.GetString(1), "Function", reader.GetString(0), reader.GetString(1));
        }
    }

    private async Task AddRowsAsync(TreeViewItem parent, string sql, ExplorerNode node)
    {
        await using var command = new SqlCommand(sql, connection); command.Parameters.AddWithValue("@schema", node.Schema); command.Parameters.AddWithValue("@name", node.Name); await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            completionSymbols.Add(reader.GetString(0));
            parent.Items.Add(new TreeViewItem { Header = reader.GetString(0) + (reader.IsDBNull(1) ? "" : "  ·  " + reader.GetValue(1)) });
        }
    }

    private async Task AddDefinitionAsync(TreeViewItem parent, ExplorerNode node)
    {
        await using var command = new SqlCommand("SELECT OBJECT_DEFINITION(OBJECT_ID(QUOTENAME(@schema)+'.'+QUOTENAME(@name)))", connection); command.Parameters.AddWithValue("@schema", node.Schema); command.Parameters.AddWithValue("@name", node.Name); var value = await command.ExecuteScalarAsync(); parent.Items.Add(new TreeViewItem { Header = value is null ? "Tanım okunamadı" : "Tanım hazır (çift tıklayıp sorguya ekleyebilirsin)" });
    }

    private void SchemaTree_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source) return;
        var item = FindParent<TreeViewItem>(source);
        if (item is null || item.Tag is not ExplorerNode node) { e.Handled = true; return; }
        item.IsSelected = true;
        var menu = new ContextMenu();
        if (node.Kind == "Database")
        {
            menu.Items.Add(CreateContextMenuItem("Şemayı yenile", async (_, _) => await RefreshSchemaFromContextAsync()));
            menu.Items.Add(CreateContextMenuItem("Bağlantıyı kes", (_, _) => Disconnect_Click(this, new RoutedEventArgs())));
            menu.Items.Add(new Separator());
        }
        else if (node.Kind is "Table" or "View")
        {
            menu.Items.Add(CreateContextMenuItem(node.Kind == "View" ? "New View..." : "Aç", async (_, _) => await OpenObjectAsync(node)));
            menu.Items.Add(CreateContextMenuItem("Design", (_, _) => ShowObjectMessage(node, "Tasarım ekranı sonraki adımda açılacak.")));
            menu.Items.Add(CreateContextMenuItem("Select Top 1000 Rows", async (_, _) => await OpenSelectQueryAsync(node)));
            menu.Items.Add(CreateContextMenuItem("Edit Top 200 Rows", async (_, _) => await OpenSelectQueryAsync(node)));
            menu.Items.Add(CreateContextMenuItem("View Dependencies", (_, _) => ShowObjectMessage(node, "Bağımlılık görünümü hazırlanıyor.")));
            menu.Items.Add(new Separator());
            menu.Items.Add(CreateContextMenuItem("Script as", (_, _) => ShowObjectMessage(node, "Script seçenekleri hazırlanıyor.")));
            menu.Items.Add(CreateContextMenuItem("Properties", (_, _) => ShowObjectMessage(node, $"{node.Schema}.{node.Name}")));
            menu.Items.Add(new Separator());
        }
        else if (node.Kind is "Procedure" or "Function")
        {
            menu.Items.Add(CreateContextMenuItem("Execute", async (_, _) => await OpenDefinitionQueryAsync(node)));
            menu.Items.Add(CreateContextMenuItem("Modify", async (_, _) => await OpenDefinitionQueryAsync(node)));
            menu.Items.Add(CreateContextMenuItem("Script as", (_, _) => ShowObjectMessage(node, "Script seçenekleri hazırlanıyor.")));
            menu.Items.Add(new Separator());
            menu.Items.Add(CreateContextMenuItem("Properties", (_, _) => ShowObjectMessage(node, $"{node.Schema}.{node.Name}")));
            menu.Items.Add(new Separator());
        }
        if (node.Kind != "Database")
        {
            menu.Items.Add(CreateContextMenuItem("Adı kopyala", (_, _) => Clipboard.SetText($"[{node.Schema}].[{node.Name}]")));
            menu.Items.Add(CreateContextMenuItem("Şemayı yenile", async (_, _) => await RefreshSchemaFromContextAsync()));
        }
        item.ContextMenu = menu;
    }

    private void ShowObjectMessage(ExplorerNode node, string message) => MessageBox.Show(this, message, $"{node.Schema}.{node.Name}", MessageBoxButton.OK, MessageBoxImage.Information);

    private void SchemaTree_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source) return;
        var item = FindParent<TreeViewItem>(source);
        if (item?.Tag is ExplorerNode node && node.Kind is "Table" or "View" or "Procedure" or "Function")
        {
            _ = OpenObjectAsync(node);
            e.Handled = true;
        }
    }

    private static MenuItem CreateContextMenuItem(string header, RoutedEventHandler handler)
    {
        var item = new MenuItem { Header = header, Padding = new Thickness(10, 6, 24, 6) };
        item.Click += handler;
        return item;
    }

    private async Task OpenObjectAsync(ExplorerNode node)
    {
        if (node.Kind is "Procedure" or "Function") await OpenDefinitionQueryAsync(node);
        else await OpenSelectQueryAsync(node);
    }

    private async Task OpenSelectQueryAsync(ExplorerNode node)
    {
        var sql = $"SELECT TOP (1000) *\nFROM [{node.Schema}].[{node.Name}];";
        AddQueryTab($"{node.Name}.sql", sql);
    }

    private async Task OpenDefinitionQueryAsync(ExplorerNode node)
    {
        if (connection is null) return;
        await using var command = new SqlCommand("SELECT OBJECT_DEFINITION(OBJECT_ID(QUOTENAME(@schema)+'.'+QUOTENAME(@name)))", connection);
        command.Parameters.AddWithValue("@schema", node.Schema);
        command.Parameters.AddWithValue("@name", node.Name);
        var definition = await command.ExecuteScalarAsync() as string;
        AddQueryTab($"{node.Name}.sql", definition ?? $"-- [{node.Schema}].[{node.Name}] tanımı okunamadı");
    }

    private void AddQueryTab(string name, string text)
    {
        SaveActiveQuery();
        queryNumber++;
        var tab = new TabItem { Tag = text };
        SetTabHeader(tab, name);
        QueryTabs.Items.Add(tab);
        QueryTabs.SelectedItem = tab;
    }

    private async Task RefreshSchemaFromContextAsync()
    {
        await LoadSchemaAsync();
        StatusText.Text = "Şema yenilendi";
    }

    private static T? FindParent<T>(DependencyObject? current) where T : DependencyObject
    {
        while (current is not null)
        {
            if (current is T result) return result;
            current = current is Visual visual ? VisualTreeHelper.GetParent(visual) : LogicalTreeHelper.GetParent(current);
        }
        return null;
    }

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject root) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match) yield return match;
            foreach (var descendant in FindVisualChildren<T>(child)) yield return descendant;
        }
    }

    private void ExplorerFilter_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        var filter = ExplorerFilter.Text.Trim();
        foreach (var item in FindTreeItems(SchemaTree.Items))
            item.Visibility = string.IsNullOrWhiteSpace(filter) || item.Header?.ToString()?.Contains(filter, StringComparison.OrdinalIgnoreCase) == true ? Visibility.Visible : Visibility.Collapsed;
    }

    private static IEnumerable<TreeViewItem> FindTreeItems(ItemCollection items)
    {
        foreach (TreeViewItem item in items)
        {
            yield return item;
            foreach (var child in FindTreeItems(item.Items)) yield return child;
        }
    }

    private void SqlEditor_TextChanged(object? sender, EventArgs e)
    {
        if (suppressEditorChanges) return;
        var text = GetEditorText();
        var caret = GetCaretOffset();
        var beforeCaret = text[..Math.Min(caret, text.Length)];
        TryInsertAutomaticAlias(beforeCaret, caret);
        if (insertingAutomaticAlias) return;
        var shouldSuggest = beforeCaret.EndsWith(".", StringComparison.Ordinal) || Regex.IsMatch(beforeCaret, @"[A-Za-z_]\w{1,}$");
        ShowIntelliSense(shouldSuggest);
    }

    private void TryInsertAutomaticAlias(string beforeCaret, int caret)
    {
        if (insertingAutomaticAlias || caret != GetEditorText().Length || !beforeCaret.EndsWith(" ", StringComparison.Ordinal)) return;
        var match = Regex.Match(beforeCaret, @"\b(?:FROM|JOIN)\s+(?:(?:\[[^\]]+\]|[A-Za-z_]\w*)\.)?(?<table>\[[^\]]+\]|[A-Za-z_]\w*)\s$", RegexOptions.IgnoreCase);
        if (!match.Success) return;
        var table = UnquoteIdentifier(match.Groups["table"].Value);
        if (!tableNames.Contains(table)) return;
        var alias = table[..1].ToLowerInvariant();
        insertingAutomaticAlias = true;
        SqlEditor.Document.Insert(caret, alias + " ");
        SqlEditor.CaretOffset = caret + alias.Length + 1;
        insertingAutomaticAlias = false;
    }

    private void SqlEditor_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers == ModifierKeys.Shift && e.Key == Key.D9) { InsertPair("()"); e.Handled = true; return; }
        if (e.Key == Key.OemOpenBrackets) { InsertPair("[]"); e.Handled = true; return; }
        if (e.Key == Key.Space && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            ShowIntelliSense(true);
            e.Handled = true;
            return;
        }

        if (!IntelliSensePopup.IsOpen) return;
        if (e.Key == Key.Escape) { IntelliSensePopup.IsOpen = false; e.Handled = true; }
        else if (e.Key == Key.Down) { MoveCompletion(1); e.Handled = true; }
        else if (e.Key == Key.Up) { MoveCompletion(-1); e.Handled = true; }
        else if (e.Key is Key.Enter or Key.Tab) { InsertSelectedCompletion(); e.Handled = true; }
    }

    private void ShowIntelliSense(bool force)
    {
        var editorText = GetEditorText();
        var caretIndex = GetCaretOffset();
        var beforeCaret = editorText[..Math.Min(caretIndex, editorText.Length)];
        var qualifiedMatch = Regex.Match(beforeCaret, @"(?<qualifier>[A-Za-z_]\w*)\.(?<token>[A-Za-z_]\w*)?$", RegexOptions.IgnoreCase);
        var qualifier = qualifiedMatch.Success ? qualifiedMatch.Groups["qualifier"].Value : "";
        var start = beforeCaret.Length - 1;
        while (start >= 0 && (char.IsLetterOrDigit(beforeCaret[start]) || beforeCaret[start] is '_' or '#' or '@')) start--;
        var token = qualifiedMatch.Success ? qualifiedMatch.Groups["token"].Value : beforeCaret[(start + 1)..];
        if (!force && token.Length < 2 && !beforeCaret.EndsWith(".", StringComparison.Ordinal)) { IntelliSensePopup.IsOpen = false; return; }

        var aliases = GetTableAliases(beforeCaret);
        var qualifiedTable = aliases.TryGetValue(qualifier, out var aliasTable) ? aliasTable : "";
        if (string.IsNullOrWhiteSpace(qualifiedTable) && !string.IsNullOrWhiteSpace(qualifier))
            qualifiedTable = ResolveTableForAlias(beforeCaret, qualifier);
        var tableContext = Regex.IsMatch(beforeCaret, @"\b(FROM|JOIN)\s+(?:[A-Za-z_]\w*\.)?[A-Za-z_]*$", RegexOptions.IgnoreCase);
        var candidateSource = !string.IsNullOrWhiteSpace(qualifiedTable) && tableColumns.TryGetValue(qualifiedTable, out var columns)
            ? columns.Concat(["*"])
            : qualifiedMatch.Success
                ? []
            : tableContext
                ? sqlKeywords.Concat(tableNames).Concat(completionSymbols.Where(value => !tableNames.Contains(value)))
                : sqlKeywords.Concat(tableNames);
        var candidates = candidateSource
            .Where(value => force || value.StartsWith(token, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value.StartsWith(token, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(value => value)
            .Take(30)
            .Select(value => new CompletionItem(value, tableContext && tableNames.Contains(value) ? $"{value} {value[..1].ToLowerInvariant()}" : value))
            .ToList();
        IntelliSenseList.ItemsSource = candidates;
        IntelliSenseList.SelectedIndex = candidates.Count == 0 ? -1 : 0;
        if (candidates.Count > 0)
        {
            var caretRect = SqlEditor.TextArea.Caret.CalculateCaretRectangle();
            IntelliSensePopup.Placement = PlacementMode.Relative;
            IntelliSensePopup.HorizontalOffset = Math.Max(0, caretRect.X);
            IntelliSensePopup.VerticalOffset = Math.Max(0, caretRect.Bottom + 4);
        }
        IntelliSensePopup.IsOpen = candidates.Count > 0;
    }

    private Dictionary<string, string> GetTableAliases(string text)
    {
        var aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in Regex.Matches(text, @"\b(?:FROM|JOIN)\s+(?:(?<schema>\[[^\]]+\]|[A-Za-z_]\w*)\.)?(?<table>\[[^\]]+\]|[A-Za-z_]\w*)(?:\s+(?:AS\s+)?(?<alias>[A-Za-z_]\w*))?", RegexOptions.IgnoreCase))
        {
            var table = UnquoteIdentifier(match.Groups["table"].Value);
            var schema = UnquoteIdentifier(match.Groups["schema"].Value);
            var alias = match.Groups["alias"].Value;
            if (string.IsNullOrWhiteSpace(alias) || sqlKeywords.Contains(alias, StringComparer.OrdinalIgnoreCase)) alias = table[..1].ToLowerInvariant();
            aliases[alias] = string.IsNullOrWhiteSpace(schema) ? table : $"{schema}.{table}";
        }
        return aliases;
    }

    private static string UnquoteIdentifier(string value) => value.Trim().Trim('[', ']');

    private string ResolveTableForAlias(string text, string alias)
    {
        const string pattern = @"(?:FROM|JOIN)\s+(?<source>(?:\[[^\]]+\]|[A-Za-z_]\w*)(?:\s*\.\s*(?:\[[^\]]+\]|[A-Za-z_]\w*))?)\s+(?:AS\s+)?(?<alias>[A-Za-z_]\w*)\b";
        foreach (Match match in Regex.Matches(text, pattern, RegexOptions.IgnoreCase))
        {
            if (!string.Equals(match.Groups["alias"].Value, alias, StringComparison.OrdinalIgnoreCase)) continue;
            var source = match.Groups["source"].Value.Replace(" ", "", StringComparison.Ordinal);
            var parts = source.Split('.', StringSplitOptions.RemoveEmptyEntries);
            return parts.Length == 2 ? $"{UnquoteIdentifier(parts[0])}.{UnquoteIdentifier(parts[1])}" : UnquoteIdentifier(source);
        }
        return "";
    }

    private void MoveCompletion(int offset)
    {
        if (IntelliSenseList.Items.Count == 0) return;
        var next = Math.Clamp(IntelliSenseList.SelectedIndex + offset, 0, IntelliSenseList.Items.Count - 1);
        IntelliSenseList.SelectedIndex = next;
    }

    private void InsertSelectedCompletion()
    {
        if (IntelliSenseList.SelectedItem is not CompletionItem selected) return;
        var editorText = GetEditorText();
        var caret = GetCaretOffset();
        var start = caret - 1;
        while (start >= 0 && (char.IsLetterOrDigit(editorText[start]) || editorText[start] is '_' or '#' or '@')) start--;
        suppressEditorChanges = true;
        SqlEditor.Select(start + 1, caret - start - 1);
        SqlEditor.SelectedText = selected.InsertText;
        var newCaret = start + 1 + selected.InsertText.Length;
        SqlEditor.Select(newCaret, 0);
        SqlEditor.CaretOffset = newCaret;
        SqlEditor.Focus();
        suppressEditorChanges = false;
        IntelliSensePopup.IsOpen = false;
    }

    private string GetEditorText() => SqlEditor.Text;

    private void SetEditorText(string text)
    {
        suppressEditorChanges = true;
        SqlEditor.Text = text;
        SqlEditor.CaretOffset = SqlEditor.Text.Length;
        suppressEditorChanges = false;
    }

    private int GetCaretOffset() => SqlEditor.CaretOffset;

    private void InsertPair(string pair)
    {
        suppressEditorChanges = true;
        var start = SqlEditor.SelectionStart;
        SqlEditor.SelectedText = pair;
        SqlEditor.CaretOffset = start + 1;
        suppressEditorChanges = false;
    }

    private async void Execute_Click(object sender, RoutedEventArgs e)
    {
        if (connection is null) { MessageBox.Show(this, "Önce bir SQL Server bağlantısı kurun.", "Bağlantı gerekli"); return; }
        var sql = SqlEditor.SelectedText.Length > 0 ? SqlEditor.SelectedText : GetEditorText();
        if (string.IsNullOrWhiteSpace(sql)) return;
        queryCancellation?.Cancel();
        queryCancellation = new CancellationTokenSource();
        var stopwatch = Stopwatch.StartNew();
        try
        {
            StatusText.Text = "Çalışıyor...";
            var tables = new List<DataTable>();
            var affectedRows = 0;
            var batches = Regex.Split(sql, @"(?im)^\s*GO(?:\s+\d+)?\s*(?:--.*)?$")
                .Where(batch => !string.IsNullOrWhiteSpace(batch)).ToList();
            foreach (var batch in batches)
            {
                await using var command = new SqlCommand(batch.Trim(), connection) { CommandTimeout = 60 };
                await using var reader = await command.ExecuteReaderAsync(queryCancellation.Token);
                var dataSet = new DataSet();
                dataSet.Load(reader, LoadOption.Upsert, Enumerable.Range(1, 64).Select(index => $"Result{index}").ToArray());
                tables.AddRange(dataSet.Tables.Cast<DataTable>().Where(table => table.Columns.Count > 0));
                if (reader.RecordsAffected > 0) affectedRows += reader.RecordsAffected;
            }

            var duration = stopwatch.ElapsedMilliseconds;
            queryHistory.Insert(0, new QueryHistoryEntry { StartedAt = DateTimeOffset.Now, Sql = sql, Server = connection.DataSource, Database = connection.Database, DurationMs = duration, ResultSets = tables.Count, Rows = tables.Sum(table => table.Rows.Count), AffectedRows = affectedRows, Succeeded = true });
            WorkspaceStores.AddHistory(queryHistory[0]);
            if (tables.Count == 0) { MessagesBox.Text = affectedRows > 0 ? $"Sorgu tamamlandı. Etkilenen satır: {affectedRows:N0}" : "Sorgu sonuç döndürmedi."; StatusText.Text = "Hazır"; TimingText.Text = $"{affectedRows:N0} etkilenen satır · {duration:N0} ms"; return; }

            var table = tables[0];
            resultView = table.DefaultView;
            resultFilters.Clear();
            ResultsGrid.ItemsSource = resultView;
            PrimaryResultsTab.Header = $"Sonuç 1 · {table.Rows.Count:N0} satır";
            for (var tabIndex = ResultSetsTabs.Items.Count - 2; tabIndex >= 1; tabIndex--) ResultSetsTabs.Items.RemoveAt(tabIndex);
            for (var index = 1; index < tables.Count; index++)
            {
                var extraTable = tables[index];
                var extraGrid = CreateResultsGrid(extraTable.DefaultView);
                ResultSetsTabs.Items.Insert(index, new TabItem { Header = $"Sonuç {index + 1} · {extraTable.Rows.Count:N0} satır", Content = extraGrid });
            }
            ResultSetsTabs.SelectedIndex = 0;
            ResultSummaryText.Text = $"  {tables.Count} sonuç kümesi · {table.Rows.Count:N0} satır · Başlıklara tıklayarak toplam al";
            UpdateResultTotals();
            stopwatch.Stop();
            TimingText.Text = $"{table.Rows.Count:N0} satır · {affectedRows:N0} etkilenen · {duration:N0} ms";
            MessagesBox.Text = "Sorgu başarıyla tamamlandı.";
            StatusText.Text = "Hazır";
        }
        catch (OperationCanceledException) { StatusText.Text = "İptal edildi"; MessagesBox.Text = "Sorgu kullanıcı tarafından durduruldu."; }
        catch (Exception ex) { queryHistory.Insert(0, new QueryHistoryEntry { StartedAt = DateTimeOffset.Now, Sql = sql, Server = connection?.DataSource ?? "", Database = connection?.Database ?? "", DurationMs = stopwatch.ElapsedMilliseconds, Succeeded = false, Error = ex.Message }); if (queryHistory[0] is { } failed) WorkspaceStores.AddHistory(failed); StatusText.Text = "Hata"; ShowError("Sorgu çalıştırılamadı", ex); }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => queryCancellation?.Cancel();

    private DataGrid CreateResultsGrid(DataView view)
    {
        var grid = new DataGrid
        {
            ItemsSource = view,
            AutoGenerateColumns = true,
            IsReadOnly = true,
            CanUserSortColumns = true,
            CanUserResizeColumns = true,
            CanUserReorderColumns = true,
            SelectionMode = DataGridSelectionMode.Extended,
            SelectionUnit = DataGridSelectionUnit.CellOrRowHeader,
            ClipboardCopyMode = DataGridClipboardCopyMode.IncludeHeader,
            CanUserAddRows = false,
            GridLinesVisibility = DataGridGridLinesVisibility.Horizontal,
            HeadersVisibility = DataGridHeadersVisibility.Column,
            RowBackground = Brushes.White,
            AlternatingRowBackground = new SolidColorBrush(Color.FromRgb(251, 252, 254)),
            BorderThickness = new Thickness(0),
            ColumnHeaderHeight = 36,
            RowHeight = 30,
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.FromRgb(31, 41, 55))
        };
        grid.AutoGeneratingColumn += ResultsGrid_AutoGeneratingColumn;
        grid.Sorting += ResultsGrid_Sorting;
        return grid;
    }

    private void ResultsGrid_Sorting(object sender, DataGridSortingEventArgs e)
    {
        if (sender is not DataGrid grid || e.Column.SortMemberPath is not string columnName || grid.ItemsSource is not DataView view) return;
        if (view.Table is not DataTable table) return;
        var column = table.Columns[columnName];
        if (column is null) return;
        var numericTypes = new[] { typeof(byte), typeof(short), typeof(int), typeof(long), typeof(float), typeof(double), typeof(decimal) };
        if (!numericTypes.Contains(column.DataType))
        {
            ResultTotalsText.Text = $"{columnName}: sayısal toplam yok · {view.Count:N0} satır";
            return;
        }
        decimal total = 0;
        foreach (DataRowView row in view)
        {
            if (row.Row.IsNull(column)) continue;
            try { total += Convert.ToDecimal(row.Row[column], System.Globalization.CultureInfo.InvariantCulture); }
            catch (FormatException) { }
            catch (InvalidCastException) { }
        }
        ResultTotalsText.Text = $"SUM({columnName}) = {total:N8}   ·   {view.Count:N0} hücre   ·   filtrelenmiş görünüm";
    }

    private void ResultsGrid_AutoGeneratingColumn(object? sender, DataGridAutoGeneratingColumnEventArgs e)
    {
        var columnName = e.PropertyName;
        var title = new TextBlock { Text = columnName, FontWeight = FontWeights.SemiBold, Margin = new Thickness(2, 0, 2, 0) };
        e.Column.Header = title;
    }

    private void ResultColumnFilter_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (sender is not TextBox filter || filter.Tag is not string columnName || resultView is null) return;
        var value = filter.Text.Trim();
        if (value.Length == 0) resultFilters.Remove(columnName); else resultFilters[columnName] = value;
        try
        {
            resultView.RowFilter = string.Join(" AND ", resultFilters.Select(item =>
                $"CONVERT([{item.Key.Replace("]", "]]", StringComparison.Ordinal)}], 'System.String') LIKE '%{item.Value.Replace("'", "''", StringComparison.Ordinal)}%'"));
            UpdateResultTotals();
        }
        catch (EvaluateException)
        {
            resultView.RowFilter = "";
            UpdateResultTotals();
        }
    }

    private void UpdateResultTotals()
    {
        if (resultView?.Table is not DataTable table)
        {
            ResultTotalsText.Text = "Toplamlar: —";
            return;
        }

        var numericTypes = new[] { typeof(byte), typeof(short), typeof(int), typeof(long), typeof(float), typeof(double), typeof(decimal) };
        var totals = new List<string>();
        foreach (DataColumn column in table.Columns)
        {
            if (!numericTypes.Contains(column.DataType)) continue;
            decimal total = 0;
            foreach (DataRowView row in resultView)
            {
                if (row.Row.IsNull(column)) continue;
                try { total += Convert.ToDecimal(row.Row[column], System.Globalization.CultureInfo.InvariantCulture); }
                catch (FormatException) { }
                catch (InvalidCastException) { }
            }
            totals.Add($"{column.ColumnName}: {total:N2}");
        }
        ResultTotalsText.Text = totals.Count == 0
            ? $"Toplamlar: sayısal sütun yok · {resultView.Count:N0} görünen satır"
            : $"Toplamlar · {resultView.Count:N0} görünen satır:  " + string.Join("   ·   ", totals);
    }

    private void CopyResults_Click(object sender, RoutedEventArgs e)
    {
        if (ResultsGrid.SelectedItems.Count == 0) return;
        ApplicationCommands.Copy.Execute(null, ResultsGrid);
    }

    private void ClearResultFilters_Click(object sender, RoutedEventArgs e)
    {
        resultFilters.Clear();
        if (resultView is not null) resultView.RowFilter = string.Empty;
        ResultSummaryText.Text = resultView is null ? "  Sorgu sonucu" : $"  {resultView.Count:N0} satır · Filtre yok";
        UpdateResultTotals();
        foreach (var filter in FindVisualChildren<TextBox>(ResultsGrid))
            if (filter.Tag is string) filter.Clear();
    }

    private void Disconnect_Click(object sender, RoutedEventArgs e) { queryCancellation?.Cancel(); connection?.Dispose(); connection = null; SchemaTree.Items.Clear(); ConnectionLabel.Text = "Bağlantı yok"; TopConnectionText.Text = "  Bağlantı yok"; StatusText.Text = "Bağlı değil"; }
    private async void RefreshSchema_Click(object sender, RoutedEventArgs e) { try { await LoadSchemaAsync(); StatusText.Text = "Şema yenilendi"; } catch (Exception ex) { ShowError("Şema yüklenemedi", ex); } }
    private void NewQuery_Click(object sender, RoutedEventArgs e)
    {
        SaveActiveQuery();
        queryNumber++;
        var tab = new TabItem { Tag = "" };
        SetTabHeader(tab, $"SQLQuery{queryNumber}.sql");
        QueryTabs.Items.Add(tab);
        QueryTabs.SelectedItem = tab;
    }

    private void SetTabHeader(TabItem tab, string name)
    {
        var header = new StackPanel { Orientation = Orientation.Horizontal };
        header.Children.Add(new TextBlock { Text = $"●  {name}", VerticalAlignment = VerticalAlignment.Center });
        var close = new Button { Content = "×", Tag = tab, Padding = new Thickness(5, 0, 5, 0), Margin = new Thickness(8, 0, 0, 0), ToolTip = "Sorgu sekmesini kapat" };
        close.Click += CloseQueryTab_Click;
        header.Children.Add(close);
        tab.Header = header;
    }

    private void CloseQueryTab_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: TabItem tab }) return;
        if (QueryTabs.Items.Count == 1)
        {
            SetEditorText("");
            return;
        }
        if (tab == activeQueryTab) SaveActiveQuery();
        var index = QueryTabs.Items.IndexOf(tab);
        QueryTabs.Items.Remove(tab);
        QueryTabs.SelectedIndex = Math.Max(0, Math.Min(index, QueryTabs.Items.Count - 1));
        e.Handled = true;
    }

    private void QueryTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.Source != QueryTabs || QueryTabs.SelectedItem is not TabItem selected || selected == activeQueryTab) return;
        SaveActiveQuery();
        activeQueryTab = selected;
        SetEditorText(selected.Tag as string ?? "");
    }

    private void SaveActiveQuery()
    {
        if (activeQueryTab is not null) activeQueryTab.Tag = GetEditorText();
    }
    private void Exit_Click(object sender, RoutedEventArgs e) => Close();
    private void SqlEditor_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.F5) { Execute_Click(sender, e); e.Handled = true; } }
    private async void CheckForUpdates_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            using var client = new HttpClient();
            client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("MKASqlStudio", AppVersion));
            var localVersion = Version.Parse(AppVersion);
            using var versionResponse = await client.GetAsync($"https://raw.githubusercontent.com/karamuklu/MKASqlStudio/main/version.json?ts={DateTimeOffset.UtcNow.ToUnixTimeSeconds()}");
            Version? remoteVersion = null;
            if (versionResponse.IsSuccessStatusCode)
            {
                using var versionDocument = JsonDocument.Parse(await versionResponse.Content.ReadAsStringAsync());
                if (Version.TryParse(versionDocument.RootElement.GetProperty("version").GetString(), out var parsedVersion)) remoteVersion = parsedVersion;
            }
            if (remoteVersion is not null && remoteVersion <= localVersion)
            {
                MessageBox.Show(this, $"Son sürüm yüklü.\nKurulu sürüm: v{AppVersion}", "Güncelleme");
                return;
            }
            using var response = await client.GetAsync("https://api.github.com/repos/karamuklu/MKASqlStudio/releases/latest");
            if (!response.IsSuccessStatusCode) { MessageBox.Show(this, "Henüz yayınlanmış bir sürüm bulunamadı. Güncellemeler GitHub Releases üzerinden yayınlanacak.", "Güncelleme"); return; }
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var tag = document.RootElement.GetProperty("tag_name").GetString() ?? "";
            var asset = document.RootElement.GetProperty("assets").EnumerateArray().FirstOrDefault(item => string.Equals(item.GetProperty("name").GetString(), "WindowsMssqlStudio-release.zip", StringComparison.OrdinalIgnoreCase));
            if (asset.ValueKind == JsonValueKind.Undefined) { MessageBox.Show(this, "Release bulundu fakat indirilebilir ZIP paketi yok.", "Güncelleme"); return; }
            var downloadUrl = asset.GetProperty("browser_download_url").GetString()!;
            var tempRoot = Path.Combine(Path.GetTempPath(), "MkaSqlStudioUpdate");
            Directory.CreateDirectory(tempRoot);
            var zipPath = Path.Combine(tempRoot, "update.zip");
            var extractPath = Path.Combine(tempRoot, "files");
            if (Directory.Exists(extractPath)) Directory.Delete(extractPath, true);
            await File.WriteAllBytesAsync(zipPath, await client.GetByteArrayAsync(downloadUrl));
            ZipFile.ExtractToDirectory(zipPath, extractPath);
            var installPath = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
            var scriptPath = Path.Combine(tempRoot, "apply-update.ps1");
            var currentPid = Environment.ProcessId;
            var escapedExtract = extractPath.Replace("'", "''");
            var escapedInstall = installPath.Replace("'", "''");
            var script = $"$ErrorActionPreference = 'Stop'\n$source = '{escapedExtract}'\n$target = '{escapedInstall}'\n$currentPid = {currentPid}\nfor ($i = 0; $i -lt 30; $i++) {{ if (-not (Get-Process -Id $currentPid -ErrorAction SilentlyContinue)) {{ break }} Start-Sleep -Milliseconds 500 }}\nNew-Item -ItemType Directory -Force -Path $target | Out-Null\nCopy-Item -Path (Join-Path $source '*') -Destination $target -Recurse -Force\nStart-Process -FilePath (Join-Path $target 'WindowsMssqlStudio.exe') -WorkingDirectory $target\n";
            await File.WriteAllTextAsync(scriptPath, script);
            MessageBox.Show(this, $"v{remoteVersion?.ToString(3) ?? tag} güncellemesi indirildi. Uygulama kapanıp yeniden başlatılacak.", "Güncelleme");
            Process.Start(new ProcessStartInfo("powershell.exe", $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{scriptPath}\"") { UseShellExecute = false, CreateNoWindow = true });
            Close();
        }
        catch (Exception ex) { ShowError("Güncelleme kontrolü başarısız", ex); }
    }

    private void About_Click(object sender, RoutedEventArgs e) => MessageBox.Show(this, $"MKA SQL Studio\nWindows MSSQL çalışma alanı\nSürüm v{AppVersion}", "Hakkında");
    private void ShowError(string title, Exception ex) { MessagesBox.Text = ex.Message; MessageBox.Show(this, ex.Message, title, MessageBoxButton.OK, MessageBoxImage.Error); }
}



