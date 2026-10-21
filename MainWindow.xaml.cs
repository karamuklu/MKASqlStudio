using System.Data;
using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Controls;
using Microsoft.Data.SqlClient;

namespace WindowsMssqlStudio;

public partial class MainWindow
{
    private SqlConnection? connection;
    private CancellationTokenSource? queryCancellation;

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
        SchemaTree.Items.Clear();
        var database = new TreeViewItem { Header = connection.Database, IsExpanded = true };
        var tables = new TreeViewItem { Header = "Tables" };
        var views = new TreeViewItem { Header = "Views" };
        var programmable = new TreeViewItem { Header = "Programmability" };
        await using var command = new SqlCommand(@"SELECT TABLE_SCHEMA, TABLE_NAME, TABLE_TYPE
            FROM INFORMATION_SCHEMA.TABLES ORDER BY TABLE_TYPE, TABLE_SCHEMA, TABLE_NAME", connection);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var item = new TreeViewItem { Header = $"{reader.GetString(0)}.{reader.GetString(1)}" };
            item.Tag = reader.GetString(1);
            if (reader.GetString(2) == "VIEW") views.Items.Add(item); else tables.Items.Add(item);
        }
        database.Items.Add(tables);
        database.Items.Add(views);
        database.Items.Add(programmable);
        SchemaTree.Items.Add(database);
        tables.IsExpanded = true;
        views.IsExpanded = true;
    }

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
    private void ShowError(string title, Exception ex) { MessagesBox.Text = ex.Message; MessageBox.Show(this, ex.Message, title, MessageBoxButton.OK, MessageBoxImage.Error); }
}
