using System.ComponentModel;
using System.Windows;
using AeroDesk.Core.Settings;
using AeroDesk.Services;
using AeroDesk.ViewModels;
using AvalonDock;

namespace AeroDesk;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;

    public MainWindow(AeroDeskWorkspace workspace)
    {
        InitializeComponent();
        _viewModel = new MainViewModel(workspace, new DialogService(this, workspace));
        DataContext = _viewModel;
        Closing += OnClosing;
    }

    /// <summary>Mirror a tab the user closed in AvalonDock back into the Documents collection.</summary>
    private void OnDocumentClosed(object sender, DocumentClosedEventArgs e)
    {
        if (e.Document?.Content is DocumentViewModel document)
            _viewModel.Documents.Remove(document);
    }

    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        Closing -= OnClosing;
        await _viewModel.ShutdownAsync();
    }
}
