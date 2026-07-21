using AeroDesk.Core.Connections;

namespace AeroDesk.Services;

/// <summary>What the Connect dialog hands back: where, credentials, and whether to persist.</summary>
public sealed record ConnectRequest(DfConnectionDescriptor Descriptor, string? ApiKey, bool Save);

/// <summary>UI seam so view models never touch WPF dialogs directly.</summary>
public interface IDialogService
{
    ConnectRequest? ShowConnectDialog();
    bool Confirm(string title, string message);
    void ShowError(string title, string message);
    void ShowInfo(string title, string message);
    string? PickSaveFile(string filter, string defaultFileName);
    string? PickOpenFile(string filter);
}
