using System.Windows;
using AeroDesk.Core.Connections;
using AeroDesk.Core.Settings;

namespace AeroDesk;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show(args.Exception.Message, "AeroDesk",
                MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };

        var workspace = new AeroDeskWorkspace();
        SeedFirstRunConnection(workspace);

        // `--offline` jumps straight into an in-memory demo sale (dev/demo shortcut).
        MainWindow = new MainWindow(workspace, offlineDemo: e.Args.Contains("--offline"));
        MainWindow.Show();
    }

    /// <summary>First run: a ready-made AeroBus connection (local API + the demo
    /// Keycloak realm — public endpoints, no secrets) so the sign-in dialog isn't
    /// empty on first launch; the agent just adds their email/password.</summary>
    private static void SeedFirstRunConnection(AeroDeskWorkspace workspace)
    {
        if (workspace.Connections.Count > 0) return;
        workspace.UpsertConnection(new DfConnectionDescriptor
        {
            Name = "AeroBus (localhost:5080)",
            Backend = RetailingBackend.AeroBus,
            Url = "http://localhost:5080",
            KeycloakAuthority = "https://auth.demo.aerotoys.io",
            KeycloakRealm = "aerotoys",
            KeycloakClientId = "aeroboard",
        });
    }
}
