using System.Windows;
using AeroDesk.Core.Settings;
using AeroDesk.Views;
using Microsoft.Win32;

namespace AeroDesk.Services;

public sealed class DialogService : IDialogService
{
    private readonly Window _owner;
    private readonly AeroDeskWorkspace _workspace;

    public DialogService(Window owner, AeroDeskWorkspace workspace)
    {
        _owner = owner;
        _workspace = workspace;
    }

    public ConnectRequest? ShowConnectDialog()
    {
        var dialog = new ConnectDialog(_workspace) { Owner = _owner };
        return dialog.ShowDialog() == true ? dialog.Result : null;
    }

    public bool Confirm(string title, string message) =>
        MessageBox.Show(_owner, message, title, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

    public void ShowError(string title, string message) =>
        MessageBox.Show(_owner, message, title, MessageBoxButton.OK, MessageBoxImage.Error);

    public void ShowInfo(string title, string message) =>
        MessageBox.Show(_owner, message, title, MessageBoxButton.OK, MessageBoxImage.Information);

    public string? PickSaveFile(string filter, string defaultFileName)
    {
        var dialog = new SaveFileDialog { Filter = filter, FileName = defaultFileName };
        return dialog.ShowDialog(_owner) == true ? dialog.FileName : null;
    }

    public string? PickOpenFile(string filter)
    {
        var dialog = new OpenFileDialog { Filter = filter };
        return dialog.ShowDialog(_owner) == true ? dialog.FileName : null;
    }
}
