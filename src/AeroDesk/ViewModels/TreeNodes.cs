using System.Collections.ObjectModel;
using AeroDesk.Core.Retailing;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AeroDesk.ViewModels;

/// <summary>Base for nodes in the left navigation tree.</summary>
public abstract partial class TreeNodeViewModel : ObservableObject
{
    [ObservableProperty]
    private string _name = "";

    [ObservableProperty]
    private bool _isExpanded;

    [ObservableProperty]
    private bool _isSelected;

    public string Glyph { get; protected init; } = "";
    public ObservableCollection<TreeNodeViewModel> Children { get; } = [];
}

/// <summary>A connected retailing service (DocumentForge node or offline demo).</summary>
public sealed partial class ConnectionNodeViewModel : TreeNodeViewModel
{
    private readonly MainViewModel _main;

    public IRetailingService Service { get; }

    public ConnectionNodeViewModel(MainViewModel main, IRetailingService service, string displayName)
    {
        _main = main;
        Service = service;
        Name = displayName;
        Glyph = "✈";
        IsExpanded = true;

        // Placeholders for the flows each phase lights up.
        Children.Add(new PlaceholderNodeViewModel("🛒", "New Sale — Phase 1"));
        Children.Add(new PlaceholderNodeViewModel("📋", "Orders — Phase 2"));
    }

    [RelayCommand]
    private Task DisconnectAsync() => _main.DisconnectAsync(this);
}

/// <summary>A greyed-out future feature, so the tree telegraphs the roadmap.</summary>
public sealed class PlaceholderNodeViewModel : TreeNodeViewModel
{
    public PlaceholderNodeViewModel(string glyph, string name)
    {
        Glyph = glyph;
        Name = name;
    }
}

/// <summary>Inline error display (e.g. a failed connect).</summary>
public sealed class MessageNodeViewModel : TreeNodeViewModel
{
    public MessageNodeViewModel(string message)
    {
        Glyph = "⚠";
        Name = message;
    }
}
