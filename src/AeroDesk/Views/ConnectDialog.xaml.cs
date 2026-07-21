using System.Windows;
using System.Windows.Controls;
using AeroDesk.Core.Connections;
using AeroDesk.Core.Settings;
using AeroDesk.Services;

namespace AeroDesk.Views;

/// <summary>
/// The agent sign-in: AeroBus URL + Keycloak (URL/realm/client) + agent
/// email/password — ONE login for both workbenches (reservations and departure
/// control). AeroBus is the only backend: no offline mode, no direct database
/// access. The password is stored DPAPI-encrypted when saving.
/// </summary>
public partial class ConnectDialog : Window
{
    private readonly AeroDeskWorkspace _workspace;
    private string? _selectedId;

    public ConnectRequest? Result { get; private set; }

    public ConnectDialog(AeroDeskWorkspace workspace)
    {
        _workspace = workspace;
        InitializeComponent();

        var saved = _workspace.Connections.ToList();
        SavedCombo.ItemsSource = saved;
        if (saved.Count > 0)
            SavedCombo.SelectedIndex = 0;
    }

    private void OnSavedSelected(object sender, SelectionChangedEventArgs e)
    {
        if (SavedCombo.SelectedItem is not DfConnectionDescriptor conn) return;
        _selectedId = conn.Id;
        NameBox.Text = conn.Name;
        AbUrlBox.Text = conn.Url;
        AbEmailBox.Text = conn.Email;
        AbPasswordBox.Password = _workspace.ResolveApiKey(conn) ?? "";
        AbKcUrlBox.Text = conn.KeycloakAuthority;
        AbKcRealmBox.Text = conn.KeycloakRealm;
        AbKcClientBox.Text = conn.KeycloakClientId;
    }

    private void OnConnect(object sender, RoutedEventArgs e)
    {
        var url = AbUrlBox.Text.Trim();
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https"))
        {
            ShowError("Enter a valid http(s) URL for AeroBus, e.g. http://localhost:5080.");
            return;
        }
        var kcUrl = AbKcUrlBox.Text.Trim();
        if (!Uri.TryCreate(kcUrl, UriKind.Absolute, out var kcUri) || (kcUri.Scheme != "http" && kcUri.Scheme != "https"))
        {
            ShowError("Enter a valid Keycloak URL — it is the agent login, e.g. https://auth.demo.aerotoys.io.");
            return;
        }
        if (AbEmailBox.Text.Trim().Length == 0 || AbPasswordBox.Password.Length == 0)
        {
            ShowError("Enter your agent email and password (your organisation admin creates accounts in AeroStudio).");
            return;
        }

        var name = NameBox.Text.Trim();
        if (name.Length == 0) name = $"AeroBus {uri.Host}:{uri.Port}";

        // Keep the saved connection's identity (and secret id) when it was picked from the list.
        var existing = _selectedId is null ? null : _workspace.Connections.FirstOrDefault(c => c.Id == _selectedId);
        var descriptor = (existing ?? new DfConnectionDescriptor()) with
        {
            Name = name,
            Url = url,
            Email = AbEmailBox.Text.Trim(),
            KeycloakAuthority = kcUrl,
            KeycloakRealm = string.IsNullOrWhiteSpace(AbKcRealmBox.Text) ? "aerotoys" : AbKcRealmBox.Text.Trim(),
            KeycloakClientId = string.IsNullOrWhiteSpace(AbKcClientBox.Text) ? "aeroboard" : AbKcClientBox.Text.Trim(),
        };

        Result = new ConnectRequest(descriptor, AbPasswordBox.Password,
            Save: AbSaveCheck.IsChecked == true);
        DialogResult = true;
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }
}
