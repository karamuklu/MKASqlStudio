using System.Windows;
using Microsoft.Data.SqlClient;

namespace WindowsMssqlStudio;

public partial class ConnectionDialog
{
    public string ConnectionString { get; private set; } = "";
    private SqlConnectionStringBuilder? serverBuilder;
    public ConnectionDialog() { InitializeComponent(); UpdateCredentialsState(); }
    private void AuthBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e) => UpdateCredentialsState();
    private void UpdateCredentialsState() { if (UserBox is null) return; var enabled = AuthBox.SelectedIndex == 1; UserBox.IsEnabled = enabled; PasswordBox.IsEnabled = enabled; }
    private async void LoadDatabases_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            serverBuilder = BuildBuilder("master");
            await using var serverConnection = new SqlConnection(serverBuilder.ConnectionString);
            await serverConnection.OpenAsync();
            await using var command = new SqlCommand("SELECT name FROM sys.databases WHERE state_desc = 'ONLINE' ORDER BY name", serverConnection);
            await using var reader = await command.ExecuteReaderAsync();
            DatabaseBox.Items.Clear();
            while (await reader.ReadAsync()) DatabaseBox.Items.Add(reader.GetString(0));
            if (DatabaseBox.Items.Count > 0) DatabaseBox.SelectedIndex = 0;
            DatabaseBox.IsEnabled = DatabaseBox.Items.Count > 0;
            ServerStatus.Text = $"{DatabaseBox.Items.Count} veritabanı bulundu";
        }
        catch (Exception ex) { ServerStatus.Text = ex.Message; }
    }

    private void Connect_Click(object sender, RoutedEventArgs e)
    {
        if (DatabaseBox.SelectedItem is null) { MessageBox.Show(this, "Önce sunucuya bağlanıp bir veritabanı seçin.", "Veritabanı gerekli"); return; }
        ConnectionString = BuildBuilder(DatabaseBox.SelectedItem.ToString()!).ConnectionString;
        DialogResult = true;
    }

    private SqlConnectionStringBuilder BuildBuilder(string database) { var builder = new SqlConnectionStringBuilder { DataSource = ServerBox.Text.Trim(), InitialCatalog = database, Encrypt = true, TrustServerCertificate = true, ConnectTimeout = 10 }; if (AuthBox.SelectedIndex == 0) builder.IntegratedSecurity = true; else { builder.UserID = UserBox.Text.Trim(); builder.Password = PasswordBox.Password; } return builder; }
}
