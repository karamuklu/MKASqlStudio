using System.Windows;
using Microsoft.Data.SqlClient;

namespace WindowsMssqlStudio;

public partial class ConnectionDialog
{
    public string ConnectionString { get; private set; } = "";
    public string? SelectedDatabase { get; private set; }
    private SqlConnectionStringBuilder? serverBuilder;
    public ConnectionProfile? SelectedProfile { get; private set; }

    public ConnectionDialog()
    {
        InitializeComponent();
        SavedConnectionsList.ItemsSource = ConnectionProfileStore.Load();
        UpdateCredentialsState();
    }

    private void AuthBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e) => UpdateCredentialsState();

    private void SavedConnectionsList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (SavedConnectionsList.SelectedItem is not ConnectionProfile profile) return;
        NameBox.Text = profile.Name;
        CommentBox.Text = profile.Comment;
        HostBox.Text = profile.Host;
        InstanceBox.Text = profile.Instance;
        PortBox.Text = profile.Port;
        AuthBox.SelectedIndex = profile.WindowsAuthentication ? 0 : 1;
        UserBox.Text = profile.UserName;
        PasswordBox.Password = profile.Password;
        DatabaseBox.Items.Clear();
        DatabaseBox.Items.Add(profile.Database);
        DatabaseBox.SelectedIndex = 0;
        DatabaseBox.IsEnabled = true;
    }

    private void UpdateCredentialsState()
    {
        if (UserBox is null) return;
        var enabled = AuthBox.SelectedIndex == 1;
        UserBox.IsEnabled = enabled;
        PasswordBox.IsEnabled = enabled;
    }

    private async void LoadDatabases_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            ServerStatus.Text = "Bağlanıyor...";
            serverBuilder = BuildBuilder("master");
            await using var serverConnection = new SqlConnection(serverBuilder.ConnectionString);
            await serverConnection.OpenAsync();
            await using var command = new SqlCommand("SELECT name FROM sys.databases WHERE state_desc = 'ONLINE' ORDER BY name", serverConnection);
            await using var reader = await command.ExecuteReaderAsync();
            DatabaseBox.Items.Clear();
            DatabaseBox.Items.Add("(Sunucu genelinde)");
            while (await reader.ReadAsync()) DatabaseBox.Items.Add(reader.GetString(0));
            DatabaseBox.SelectedIndex = 0;
            DatabaseBox.IsEnabled = true;
            ServerStatus.Text = $"{DatabaseBox.Items.Count - 1} veritabanı bulundu";
        }
        catch (Exception ex)
        {
            DatabaseBox.IsEnabled = false;
            ServerStatus.Text = "Bağlantı kurulamadı";
            MessageBox.Show(this, ex.Message, "SQL Server bağlantısı başarısız", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Connect_Click(object sender, RoutedEventArgs e)
    {
        if (DatabaseBox.SelectedItem is null) { MessageBox.Show(this, "Önce Sunucuya bağlan düğmesiyle bağlantıyı doğrulayın ve veritabanını seçin.", "Veritabanı gerekli"); return; }
        var selected = DatabaseBox.SelectedItem.ToString()!;
        SelectedDatabase = selected == "(Sunucu genelinde)" ? null : selected;
        ConnectionString = BuildBuilder(SelectedDatabase ?? "master").ConnectionString;
        SelectedProfile = new ConnectionProfile
        {
            Name = string.IsNullOrWhiteSpace(NameBox.Text) ? BuildDataSource() : NameBox.Text.Trim(),
            Comment = CommentBox.Text.Trim(),
            Host = HostBox.Text.Trim(),
            Instance = InstanceBox.Text.Trim(),
            Port = PortBox.Text.Trim(),
            Database = SelectedDatabase ?? "(Sunucu genelinde)",
            WindowsAuthentication = AuthBox.SelectedIndex == 0,
            UserName = UserBox.Text.Trim(),
            Password = PasswordBox.Password
        };
        DialogResult = true;
    }

    private string BuildDataSource()
    {
        var host = HostBox.Text.Trim();
        if (!string.IsNullOrWhiteSpace(InstanceBox.Text)) host += "\\" + InstanceBox.Text.Trim();
        if (!string.IsNullOrWhiteSpace(PortBox.Text) && !host.Contains(',')) host += "," + PortBox.Text.Trim();
        return host;
    }

    private SqlConnectionStringBuilder BuildBuilder(string database)
    {
        var builder = new SqlConnectionStringBuilder { DataSource = BuildDataSource(), InitialCatalog = database, Encrypt = true, TrustServerCertificate = true, ConnectTimeout = 10 };
        if (AuthBox.SelectedIndex == 0) builder.IntegratedSecurity = true;
        else { builder.UserID = UserBox.Text.Trim(); builder.Password = PasswordBox.Password; }
        return builder;
    }
}

