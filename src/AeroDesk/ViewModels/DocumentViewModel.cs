using CommunityToolkit.Mvvm.ComponentModel;

namespace AeroDesk.ViewModels;

/// <summary>Base for every tab in the AvalonDock document area.</summary>
public abstract partial class DocumentViewModel : ObservableObject
{
    [ObservableProperty]
    private string _title = "";

    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    private bool _isActive;

    public string ContentId { get; protected init; } = Guid.NewGuid().ToString("N");
    public bool CanClose { get; protected init; } = true;

    /// <summary>AvalonDock falls back to ToString() if the container-style Title binding fails.</summary>
    public override string ToString() => Title;
}

/// <summary>The startup tab.</summary>
public sealed class WelcomeDocumentViewModel : DocumentViewModel
{
    public WelcomeDocumentViewModel()
    {
        Title = "Welcome";
    }
}
