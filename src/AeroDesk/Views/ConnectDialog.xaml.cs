using System.Windows;
using System.Windows.Controls;
using AeroDesk.Core.Connections;
using AeroDesk.Core.Settings;
using AeroDesk.Services;

namespace AeroDesk.Views;

public partial class ConnectDialog : Window
{
    private readonly AeroDeskWorkspace _workspace;
    private string? _selectedId;

    public ConnectRequest? Result { get; private set; }

    public ConnectDialog(AeroDeskWorkspace workspace)
    {
        _workspace = workspace;
        InitializeComponent();

        SavedCombo.ItemsSource = _workspace.Connections;
        if (_workspace.Connections.Count > 0)
            SavedCombo.SelectedIndex = 0;
        DfRadio.Checked += (_, _) => DfPanel.IsEnabled = true;
        OfflineRadio.Checked += (_, _) => DfPanel.IsEnabled = false;
    }

    private void OnSavedSelected(object sender, SelectionChangedEventArgs e)
    {
        if (SavedCombo.SelectedItem is not DfConnectionDescriptor conn) return;
        _selectedId = conn.Id;
        NameBox.Text = conn.Name;
        UrlBox.Text = conn.Url;
        DatabaseBox.Text = conn.Database;
        ApiKeyBox.Password = _workspace.ResolveApiKey(conn) ?? "";
    }

    private void OnConnect(object sender, RoutedEventArgs e)
    {
        if (OfflineRadio.IsChecked == true)
        {
            Result = new ConnectRequest(new DfConnectionDescriptor { Name = "Offline demo" }, null, Save: false, Offline: true);
            DialogResult = true;
            return;
        }

        var url = UrlBox.Text.Trim();
        var database = DatabaseBox.Text.Trim();
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https"))
        {
            ShowError("Enter a valid http(s) URL, e.g. http://localhost:5001.");
            return;
        }
        if (database.Length == 0)
        {
            ShowError("Enter a database name (e.g. airline).");
            return;
        }

        var name = NameBox.Text.Trim();
        if (name.Length == 0) name = $"{uri.Host}:{uri.Port} ({database})";

        // Keep the saved connection's identity (and secret id) when it was picked from the list.
        var existing = _selectedId is null ? null : _workspace.Connections.FirstOrDefault(c => c.Id == _selectedId);
        var descriptor = (existing ?? new DfConnectionDescriptor()) with
        {
            Name = name,
            Url = url,
            Database = database,
        };

        var apiKey = ApiKeyBox.Password;
        Result = new ConnectRequest(descriptor, apiKey.Length == 0 ? null : apiKey,
            Save: SaveCheck.IsChecked == true, Offline: false);
        DialogResult = true;
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }
}
