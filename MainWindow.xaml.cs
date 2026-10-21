using System.Data;
using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Controls;
using Microsoft.Data.SqlClient;

namespace WindowsMssqlStudio;

public partial class MainWindow
{
    private SqlConnection? connection;
    private CancellationTokenSource? queryCancellation;
    private readonly List<(string Group, string Header)> schemaItems = new();

    public MainWindow()
    {
        InitializeComponent();
        SqlEditor.Focus();
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
            ConnectionLabel.Text = connection.DataSource + (string.IsNullOrWhiteSpace(connection.Database) ? "" : $"\n{connection.Database}");
            TopConnectionText.Text = $"  {connection.DataSource} · {connection.Database}";
            StatusText.Text = "Bağlandı";
            MessagesBox.Text = "Bağlantı başarılı.";
            await LoadSchemaAsync();
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
        schemaItems.Clear();
        await using var command = new SqlCommand(@"SELECT s.name, o.name, o.type_desc
            FROM sys.objects o
            INNER JOIN sys.schemas s ON s.schema_id = o.schema_id
            WHERE o.is_ms_shipped = 0
              AND o.type IN ('U','V','P','PC','FN','IF','TF','TR','SN','FS','FT')
            ORDER BY o.type_desc, s.name, o.name", connection);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            schemaItems.Add((MapObjectGroup(reader.GetString(2)), $"{reader.GetString(0)}.{reader.GetString(1)}"));
        }
        RebuildExplorerTree();
    }

    private static string MapObjectGroup(string typeDescription) => typeDescription switch
    {
        "USER_TABLE" => "Tables",
        "VIEW" => "Views",
        "SQL_STORED_PROCEDURE" or "CLR_STORED_PROCEDURE" => "Stored Procedures",
        "SQL_SCALAR_FUNCTION" or "SQL_TABLE_VALUED_FUNCTION" or "SQL_INLINE_TABLE_VALUED_FUNCTION" or "CLR_SCALAR_FUNCTION" or "CLR_TABLE_VALUED_FUNCTION" => "Functions",
        "SQL_TRIGGER" or "CLR_TRIGGER" => "Triggers",
        "SYNONYM" => "Synonyms",
        _ => "Other Objects"
    };

    private void RebuildExplorerTree()
    {
        SchemaTree.Items.Clear();
        var database = new TreeViewItem { Header = $"{connection?.Database}  (Database)", IsExpanded = true };
        var order = new[] { "Tables", "Views", "Stored Procedures", "Functions", "Triggers", "Synonyms", "Other Objects" };
        var filter = ExplorerFilter?.Text.Trim() ?? "";
        foreach (var groupName in order)
        {
            var matches = schemaItems.Where(item => item.Group == groupName && (filter.Length == 0 || item.Header.Contains(filter, StringComparison.OrdinalIgnoreCase))).ToList();
            if (matches.Count == 0) continue;
            var group = new TreeViewItem { Header = $"{groupName} ({matches.Count})", IsExpanded = groupName is "Tables" or "Views" };
            foreach (var match in matches) group.Items.Add(new TreeViewItem { Header = match.Header, Tag = match.Header });
            database.Items.Add(group);
        }
        SchemaTree.Items.Add(database);
    }

    private void ExplorerFilter_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) => RebuildExplorerTree();

    private async void Execute_Click(object sender, RoutedEventArgs e)
    {
        if (connection is null) { MessageBox.Show(this, "Önce bir SQL Server bağlantısı kurun.", "Bağlantı gerekli"); return; }
        var sql = SqlEditor.SelectedText.Length > 0 ? SqlEditor.SelectedText : SqlEditor.Text;
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
            ResultsGrid.ItemsSource = table.DefaultView;
            stopwatch.Stop();
            TimingText.Text = $"{table.Rows.Count:N0} satır · {stopwatch.ElapsedMilliseconds:N0} ms";
            MessagesBox.Text = "Sorgu başarıyla tamamlandı.";
            StatusText.Text = "Hazır";
        }
        catch (OperationCanceledException) { StatusText.Text = "İptal edildi"; MessagesBox.Text = "Sorgu kullanıcı tarafından durduruldu."; }
        catch (Exception ex) { StatusText.Text = "Hata"; ShowError("Sorgu çalıştırılamadı", ex); }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => queryCancellation?.Cancel();
    private void Disconnect_Click(object sender, RoutedEventArgs e) { queryCancellation?.Cancel(); connection?.Dispose(); connection = null; SchemaTree.Items.Clear(); ConnectionLabel.Text = "Bağlantı yok"; TopConnectionText.Text = "  Bağlantı yok"; StatusText.Text = "Bağlı değil"; }
    private async void RefreshSchema_Click(object sender, RoutedEventArgs e) { try { await LoadSchemaAsync(); StatusText.Text = "Şema yenilendi"; } catch (Exception ex) { ShowError("Şema yüklenemedi", ex); } }
    private void NewQuery_Click(object sender, RoutedEventArgs e) => SqlEditor.Clear();
    private void Exit_Click(object sender, RoutedEventArgs e) => Close();
    private void SqlEditor_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.F5) { Execute_Click(sender, e); e.Handled = true; } }
    private async void CheckForUpdates_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            using var client = new HttpClient();
            client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("MKASqlStudio", "0.2.0"));
            using var response = await client.GetAsync("https://api.github.com/repos/karamuklu/MKASqlStudio/releases/latest");
            if (!response.IsSuccessStatusCode) { MessageBox.Show(this, "Henüz yayınlanmış bir sürüm bulunamadı. Güncellemeler GitHub Releases üzerinden yayınlanacak.", "Güncelleme"); return; }
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var tag = document.RootElement.GetProperty("tag_name").GetString() ?? "";
            var url = document.RootElement.GetProperty("html_url").GetString() ?? "https://github.com/karamuklu/MKASqlStudio/releases";
            MessageBox.Show(this, $"Yeni yayın bulundu: {tag}\nGitHub Releases sayfası açılacak.", "Güncelleme");
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex) { ShowError("Güncelleme kontrolü başarısız", ex); }
    }

    private void About_Click(object sender, RoutedEventArgs e) => MessageBox.Show(this, "MKA SQL Studio\nWindows MSSQL çalışma alanı\nSürüm 0.2.0", "Hakkında");
    private void ShowError(string title, Exception ex) { MessagesBox.Text = ex.Message; MessageBox.Show(this, ex.Message, title, MessageBoxButton.OK, MessageBoxImage.Error); }
}
