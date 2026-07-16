using System.Collections.ObjectModel;
using AeroDesk.Core.Operations;
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

/// <summary>A connected backend. A connection can offer retailing, departure
/// control (DCS), or both — the child nodes reflect whatever this backend supports.</summary>
public sealed partial class ConnectionNodeViewModel : TreeNodeViewModel
{
    private readonly MainViewModel _main;

    public IRetailingService? Retailing { get; }
    public IOperationsService? Operations { get; }

    public ConnectionNodeViewModel(MainViewModel main, IRetailingService? retailing, IOperationsService? operations, string displayName)
    {
        _main = main;
        Retailing = retailing;
        Operations = operations;
        Name = displayName;
        Glyph = "✈";
        IsExpanded = true;

        if (retailing is not null)
        {
            Children.Add(new ActionNodeViewModel("🛒", "New Sale", () => _main.OpenSale(this)));
            Children.Add(new ActionNodeViewModel("📋", "Orders", () => _main.OpenOrders(this)));
        }
        if (operations is not null)
            Children.Add(new ActionNodeViewModel("🛫", "Departure Control", () => _main.OpenDepartureControl(this)));
    }

    [RelayCommand]
    private Task SeedDemoDataAsync() => _main.SeedDemoDataAsync(this);

    [RelayCommand]
    private Task DisconnectAsync() => _main.DisconnectAsync(this);
}

/// <summary>A clickable navigation entry (double-click or context menu → open).</summary>
public sealed partial class ActionNodeViewModel : TreeNodeViewModel
{
    private readonly Action _open;

    public ActionNodeViewModel(string glyph, string name, Action open)
    {
        Glyph = glyph;
        Name = name;
        _open = open;
    }

    [RelayCommand]
    private void Open() => _open();
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
