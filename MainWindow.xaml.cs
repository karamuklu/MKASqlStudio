using System.Data;
using System.Diagnostics;
using System.Collections.ObjectModel;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Windows.Documents;
using System.Windows;
using System.Windows.Input;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using System.Reflection;
using Microsoft.Data.SqlClient;

namespace WindowsMssqlStudio;

public partial class MainWindow
{
    private static string AppVersion => Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "0.3.0";
    private SqlConnection? connection;
    private string? serverConnectionString;
    private bool changingDatabase;
    private CancellationTokenSource? queryCancellation;
    private bool suppressEditorChanges;
    private readonly DispatcherTimer syntaxHighlightTimer = new() { Interval = TimeSpan.FromMilliseconds(180) };
    private TabItem? activeQueryTab;
    private int queryNumber = 1;
    private readonly HashSet<string> completionSymbols = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> tableNames = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<string>> tableColumns = new(StringComparer.OrdinalIgnoreCase);
    private DataView? resultView;
    private readonly Dictionary<string, string> resultFilters = new(StringComparer.OrdinalIgnoreCase);
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
        VersionText.Text = $"v{AppVersion} · SQL Server çalışma alanı";
        FooterVersionText.Text = $"MKA SQL Studio · Windows · v{AppVersion}";
        ResultsGrid.RowHeight = 28;
        ResultsGrid.EnableRowVirtualization = true;
        ResultsGrid.EnableColumnVirtualization = true;
        syntaxHighlightTimer.Tick += SyntaxHighlightTimer_Tick;
        Loaded += MainWindow_Loaded;
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        Loaded -= MainWindow_Loaded;
        foreach (var button in FindVisualChildren<Button>(this))
            if (string.Equals(button.Content?.ToString(), "Güncelle", StringComparison.Ordinal)) button.Content = $"v{AppVersion}  Güncelle";
        Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () =>
        {
            SetEditorText("SELECT TOP (100) name, type_desc\nFROM sys.objects\nORDER BY name;");
            activeQueryTab = QueryTabs.SelectedItem as TabItem;
            if (activeQueryTab is not null) { activeQueryTab.Tag = GetEditorText(); SetTabHeader(activeQueryTab, "SQLQuery1.sql"); }
            SqlEditor.Focus();
        });
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
            var serverBuilder = new SqlConnectionStringBuilder(dialog.ConnectionString) { InitialCatalog = "master" };
            serverConnectionString = serverBuilder.ConnectionString;
            if (dialog.SelectedProfile is not null) ConnectionProfileStore.Save(dialog.SelectedProfile);
            await LoadDatabaseSelectorAsync(dialog.SelectedDatabase);
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

    private async Task LoadDatabaseSelectorAsync(string? selectedDatabase)
    {
        if (connection is null) return;
        changingDatabase = true;
        try
        {
            DatabaseSelector.Items.Clear();
            await using var command = new SqlCommand("SELECT name FROM sys.databases WHERE state_desc = 'ONLINE' ORDER BY name", connection);
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync()) DatabaseSelector.Items.Add(reader.GetString(0));
            DatabaseSelector.IsEnabled = DatabaseSelector.Items.Count > 0;
            if (selectedDatabase is not null) DatabaseSelector.SelectedItem = selectedDatabase;
            else if (DatabaseSelector.Items.Count > 0) DatabaseSelector.SelectedIndex = 0;
        }
        finally { changingDatabase = false; }
        if (selectedDatabase is null && DatabaseSelector.SelectedItem is string database) await SwitchDatabaseAsync(database);
    }

    private async void DatabaseSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (changingDatabase || DatabaseSelector.SelectedItem is not string database || string.IsNullOrWhiteSpace(serverConnectionString)) return;
        await SwitchDatabaseAsync(database);
    }

    private async Task SwitchDatabaseAsync(string database)
    {
        if (string.IsNullOrWhiteSpace(serverConnectionString)) return;
        try
        {
            changingDatabase = true;
            var builder = new SqlConnectionStringBuilder(serverConnectionString) { InitialCatalog = database };
            connection?.Dispose();
            connection = new SqlConnection(builder.ConnectionString);
            await connection.OpenAsync();
            ConnectionLabel.Text = connection.DataSource + $"\n{database}";
            TopConnectionText.Text = $"  {connection.DataSource} · {database}";
            await LoadSchemaAsync();
            StatusText.Text = "Veritabanı seçildi";
        }
        catch (Exception ex) { ShowError("Veritabanına geçilemedi", ex); }
        finally { changingDatabase = false; }
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
        if (node.Kind is "Table" or "View" or "Procedure" or "Function")
        {
            menu.Items.Add(CreateContextMenuItem("Aç", async (_, _) => await OpenObjectAsync(node)));
            if (node.Kind is "Table" or "View") menu.Items.Add(CreateContextMenuItem("İlk 1000 satırı seç", async (_, _) => await OpenSelectQueryAsync(node)));
            if (node.Kind is "Procedure" or "Function") menu.Items.Add(CreateContextMenuItem("Tanımı düzenle", async (_, _) => await OpenDefinitionQueryAsync(node)));
            menu.Items.Add(new Separator());
        }
        menu.Items.Add(CreateContextMenuItem("Adı kopyala", (_, _) => Clipboard.SetText($"[{node.Schema}].[{node.Name}]")));
        menu.Items.Add(CreateContextMenuItem("Şemayı yenile", async (_, _) => await RefreshSchemaFromContextAsync()));
        item.ContextMenu = menu;
    }

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

    private void SqlEditor_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (suppressEditorChanges) return;
        var text = GetEditorText();
        UpdateLineNumbers(text);
        var caret = GetCaretOffset();
        var beforeCaret = text[..Math.Min(caret, text.Length)];
        var shouldSuggest = beforeCaret.EndsWith(".", StringComparison.Ordinal) || Regex.IsMatch(beforeCaret, @"[A-Za-z_]\w{1,}$");
        ShowIntelliSense(shouldSuggest);
        syntaxHighlightTimer.Stop();
        syntaxHighlightTimer.Start();
    }

    private void SqlEditor_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        LineNumbers.Margin = new Thickness(0, 14 - e.VerticalOffset, 11, 0);
    }

    private void UpdateLineNumbers(string text)
    {
        var lineCount = Math.Max(1, text.Count(character => character == '\n') + 1);
        LineNumbers.Text = string.Join(Environment.NewLine, Enumerable.Range(1, lineCount));
    }

    private void SyntaxHighlightTimer_Tick(object? sender, EventArgs e)
    {
        syntaxHighlightTimer.Stop();
        ApplySyntaxHighlighting(GetEditorText());
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
        var qualifiedMatch = Regex.Match(beforeCaret, @"(?<qualifier>[A-Za-z_]\w*)\.(?<token>[A-Za-z_]\w*)$", RegexOptions.IgnoreCase);
        var qualifier = qualifiedMatch.Success ? qualifiedMatch.Groups["qualifier"].Value : "";
        var start = beforeCaret.Length - 1;
        while (start >= 0 && (char.IsLetterOrDigit(beforeCaret[start]) || beforeCaret[start] is '_' or '#' or '@')) start--;
        var token = qualifiedMatch.Success ? qualifiedMatch.Groups["token"].Value : beforeCaret[(start + 1)..];
        if (!force && token.Length < 2 && !beforeCaret.EndsWith(".", StringComparison.Ordinal)) { IntelliSensePopup.IsOpen = false; return; }

        var aliases = GetTableAliases(beforeCaret);
        var qualifiedTable = aliases.TryGetValue(qualifier, out var aliasTable) ? aliasTable : "";
        var candidateSource = !string.IsNullOrWhiteSpace(qualifiedTable) && tableColumns.TryGetValue(qualifiedTable, out var columns) ? columns : sqlKeywords.Concat(completionSymbols);
        var tableContext = Regex.IsMatch(beforeCaret, @"\b(FROM|JOIN)\s+[A-Za-z_]*$", RegexOptions.IgnoreCase);
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
            var caretRect = SqlEditor.CaretPosition.GetCharacterRect(LogicalDirection.Forward);
            IntelliSensePopup.Placement = PlacementMode.Relative;
            IntelliSensePopup.HorizontalOffset = Math.Max(0, caretRect.X);
            IntelliSensePopup.VerticalOffset = Math.Max(0, caretRect.Bottom);
        }
        IntelliSensePopup.IsOpen = candidates.Count > 0;
    }

    private Dictionary<string, string> GetTableAliases(string text)
    {
        var aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in Regex.Matches(text, @"\b(?:FROM|JOIN)\s+(?:(?<schema>[A-Za-z_]\w*)\.)?(?<table>[A-Za-z_]\w*)(?:\s+(?:AS\s+)?(?<alias>[A-Za-z_]\w*))?", RegexOptions.IgnoreCase))
        {
            var table = match.Groups["table"].Value;
            var schema = match.Groups["schema"].Value;
            var alias = match.Groups["alias"].Value;
            if (string.IsNullOrWhiteSpace(alias) || sqlKeywords.Contains(alias, StringComparer.OrdinalIgnoreCase)) alias = table[..1].ToLowerInvariant();
            aliases[alias] = string.IsNullOrWhiteSpace(schema) ? table : $"{schema}.{table}";
        }
        return aliases;
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
        var selectionStart = GetTextPointerAtOffset(start + 1);
        var selectionEnd = GetTextPointerAtOffset(caret);
        SqlEditor.Selection.Select(selectionStart, selectionEnd);
        SqlEditor.Selection.Text = selected.InsertText;
        SqlEditor.CaretPosition = GetTextPointerAtOffset(start + 1 + selected.InsertText.Length);
        suppressEditorChanges = false;
        ApplySyntaxHighlighting(GetEditorText());
        IntelliSensePopup.IsOpen = false;
    }

    private string GetEditorText() => new TextRange(SqlEditor.Document.ContentStart, SqlEditor.Document.ContentEnd).Text.TrimEnd('\r', '\n');

    private void SetEditorText(string text)
    {
        suppressEditorChanges = true;
        SqlEditor.Document.Blocks.Clear();
        SqlEditor.Document.Blocks.Add(new Paragraph(new Run(text)) { Margin = new Thickness(0) });
        SqlEditor.CaretPosition = SqlEditor.Document.ContentEnd;
        suppressEditorChanges = false;
        ApplySyntaxHighlighting(text);
        UpdateLineNumbers(text);
    }

    private int GetCaretOffset() => new TextRange(SqlEditor.Document.ContentStart, SqlEditor.CaretPosition).Text.Length;

    private int GetTextOffset(TextPointer pointer) => new TextRange(SqlEditor.Document.ContentStart, pointer).Text.Length;

    private TextPointer GetTextPointerAtOffset(int offset)
    {
        var pointer = SqlEditor.Document.ContentStart;
        var remaining = Math.Max(0, offset);
        while (pointer is not null)
        {
            if (pointer.GetPointerContext(LogicalDirection.Forward) == TextPointerContext.Text)
            {
                var run = pointer.GetTextInRun(LogicalDirection.Forward);
                if (remaining <= run.Length) return pointer.GetPositionAtOffset(remaining, LogicalDirection.Forward) ?? SqlEditor.Document.ContentEnd;
                remaining -= run.Length;
                pointer = pointer.GetPositionAtOffset(run.Length, LogicalDirection.Forward);
                continue;
            }

            if (pointer.GetPointerContext(LogicalDirection.Forward) == TextPointerContext.ElementEnd && pointer.Parent is Paragraph)
            {
                if (remaining <= 2) return pointer;
                remaining -= 2;
            }
            pointer = pointer.GetNextContextPosition(LogicalDirection.Forward);
        }
        return SqlEditor.Document.ContentEnd;
    }

    private void ApplySyntaxHighlighting(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        if (suppressEditorChanges) return;
        var caretOffset = GetCaretOffset();
        var selectionStartOffset = GetTextOffset(SqlEditor.Selection.Start);
        var selectionEndOffset = GetTextOffset(SqlEditor.Selection.End);
        suppressEditorChanges = true;
        try
        {
            var all = new TextRange(SqlEditor.Document.ContentStart, SqlEditor.Document.ContentEnd);
            all.ApplyPropertyValue(TextElement.ForegroundProperty, new SolidColorBrush(Color.FromRgb(31, 41, 55)));
            ApplyMatches(text, @"--[^\r\n]*|/\*[\s\S]*?\*/", Color.FromRgb(22, 126, 80), FontStyles.Italic);
            ApplyMatches(text, @"'(?:''|[^'])*'|""(?:""""|[^""])*""", Color.FromRgb(180, 83, 9), FontStyles.Normal);
            ApplyMatches(text, $@"\b({string.Join("|", sqlKeywords.Select(Regex.Escape))})\b", Color.FromRgb(37, 99, 235), FontStyles.Normal, FontWeights.SemiBold);
        }
        finally
        {
            suppressEditorChanges = false;
            var selectionStart = GetTextPointerAtOffset(selectionStartOffset);
            var selectionEnd = GetTextPointerAtOffset(selectionEndOffset);
            SqlEditor.Selection.Select(selectionStart, selectionEnd);
            SqlEditor.CaretPosition = GetTextPointerAtOffset(caretOffset);
        }
    }

    private void ApplyMatches(string text, string pattern, Color color, FontStyle style, FontWeight? weight = null)
    {
        foreach (Match match in Regex.Matches(text, pattern, RegexOptions.IgnoreCase))
        {
            var range = new TextRange(GetTextPointerAtOffset(match.Index), GetTextPointerAtOffset(match.Index + match.Length));
            range.ApplyPropertyValue(TextElement.ForegroundProperty, new SolidColorBrush(color));
            range.ApplyPropertyValue(TextElement.FontStyleProperty, style);
            if (weight is not null) range.ApplyPropertyValue(TextElement.FontWeightProperty, weight.Value);
        }
    }

    private void InsertPair(string pair)
    {
        suppressEditorChanges = true;
        SqlEditor.Selection.Text = pair;
        var caret = GetCaretOffset() - 1;
        SqlEditor.CaretPosition = GetTextPointerAtOffset(caret);
        suppressEditorChanges = false;
        ApplySyntaxHighlighting(GetEditorText());
    }

    private async void Execute_Click(object sender, RoutedEventArgs e)
    {
        if (connection is null) { MessageBox.Show(this, "Önce bir SQL Server bağlantısı kurun.", "Bağlantı gerekli"); return; }
        var sql = SqlEditor.Selection.Text.Length > 0 ? SqlEditor.Selection.Text : GetEditorText();
        if (string.IsNullOrWhiteSpace(sql)) return;
        queryCancellation?.Cancel();
        queryCancellation = new CancellationTokenSource();
        var stopwatch = Stopwatch.StartNew();
        try
        {
            StatusText.Text = "Çalışıyor...";
            await using var command = new SqlCommand(sql, connection) { CommandTimeout = 60 };
            await using var reader = await command.ExecuteReaderAsync(queryCancellation.Token);
            var table = new DataTable();
            table.Load(reader);
            resultView = table.DefaultView;
            resultFilters.Clear();
            ResultsGrid.ItemsSource = resultView;
            stopwatch.Stop();
            TimingText.Text = $"{table.Rows.Count:N0} satır · {stopwatch.ElapsedMilliseconds:N0} ms";
            MessagesBox.Text = "Sorgu başarıyla tamamlandı.";
            StatusText.Text = "Hazır";
        }
        catch (OperationCanceledException) { StatusText.Text = "İptal edildi"; MessagesBox.Text = "Sorgu kullanıcı tarafından durduruldu."; }
        catch (Exception ex) { StatusText.Text = "Hata"; ShowError("Sorgu çalıştırılamadı", ex); }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => queryCancellation?.Cancel();

    private void ResultsGrid_AutoGeneratingColumn(object sender, DataGridAutoGeneratingColumnEventArgs e)
    {
        var columnName = e.PropertyName;
        var header = new FrameworkElementFactory(typeof(StackPanel));
        header.SetValue(StackPanel.OrientationProperty, Orientation.Vertical);
        header.SetValue(StackPanel.MarginProperty, new Thickness(0, 2, 0, 2));

        var title = new FrameworkElementFactory(typeof(TextBlock));
        title.SetValue(TextBlock.TextProperty, columnName);
        title.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);
        header.AppendChild(title);

        var filter = new FrameworkElementFactory(typeof(TextBox));
        filter.SetValue(TextBox.TagProperty, columnName);
        filter.SetValue(TextBox.MarginProperty, new Thickness(0, 4, 0, 0));
        filter.SetValue(TextBox.PaddingProperty, new Thickness(5, 2, 5, 2));
        filter.SetValue(TextBox.ToolTipProperty, $"{columnName} içinde filtrele");
        filter.AddHandler(TextBox.TextChangedEvent, new TextChangedEventHandler(ResultColumnFilter_TextChanged));
        header.AppendChild(filter);

        e.Column.HeaderTemplate = new DataTemplate { VisualTree = header };
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
        }
        catch (EvaluateException)
        {
            resultView.RowFilter = "";
        }
    }

    private void Disconnect_Click(object sender, RoutedEventArgs e) { queryCancellation?.Cancel(); connection?.Dispose(); connection = null; serverConnectionString = null; DatabaseSelector.Items.Clear(); DatabaseSelector.IsEnabled = false; SchemaTree.Items.Clear(); ConnectionLabel.Text = "Bağlantı yok"; TopConnectionText.Text = "  Bağlantı yok"; StatusText.Text = "Bağlı değil"; }
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
            using var versionResponse = await client.GetAsync("https://raw.githubusercontent.com/karamuklu/MKASqlStudio/main/version.json");
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
            var scriptPath = Path.Combine(tempRoot, "apply-update.cmd");
            var script = $"@echo off\r\ntimeout /t 3 /nobreak >nul\r\nrobocopy \"{extractPath}\" \"{installPath}\" /E /IS /IT >nul\r\nstart \"\" \"{Path.Combine(installPath, "WindowsMssqlStudio.exe")}\"\r\n";
            await File.WriteAllTextAsync(scriptPath, script);
            MessageBox.Show(this, $"v{remoteVersion?.ToString(3) ?? tag} güncellemesi indirildi. Uygulama kapanıp yeniden başlatılacak.", "Güncelleme");
            Process.Start(new ProcessStartInfo("cmd.exe", $"/c \"{scriptPath}\"") { UseShellExecute = false, CreateNoWindow = true });
            Close();
        }
        catch (Exception ex) { ShowError("Güncelleme kontrolü başarısız", ex); }
    }

    private void About_Click(object sender, RoutedEventArgs e) => MessageBox.Show(this, $"MKA SQL Studio\nWindows MSSQL çalışma alanı\nSürüm v{AppVersion}", "Hakkında");
    private void ShowError(string title, Exception ex) { MessagesBox.Text = ex.Message; MessageBox.Show(this, ex.Message, title, MessageBoxButton.OK, MessageBoxImage.Error); }
}


