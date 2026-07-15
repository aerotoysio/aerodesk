using System.Windows.Controls;
using System.Windows.Input;
using AeroDesk.ViewModels.Operations;

namespace AeroDesk.Views;

public partial class DeparturesView : UserControl
{
    public DeparturesView() => InitializeComponent();

    private void OnFlightDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is DeparturesDocumentViewModel vm && vm.OpenFlightCommand.CanExecute(null))
            vm.OpenFlightCommand.Execute(null);
    }
}
