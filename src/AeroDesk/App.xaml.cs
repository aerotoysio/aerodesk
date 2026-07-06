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

        MainWindow = new MainWindow(workspace);
        MainWindow.Show();
    }

    /// <summary>First run: a ready-made connection to the local dev dfdb node so
    /// the connection manager isn't empty on first launch.</summary>
    private static void SeedFirstRunConnection(AeroDeskWorkspace workspace)
    {
        if (workspace.Connections.Count > 0) return;
        workspace.UpsertConnection(new DfConnectionDescriptor
        {
            Name = "Local DocumentForge (localhost:5001)",
            Url = "http://localhost:5001",
            Database = "airline",
        });
    }
}
