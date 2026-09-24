using Avalonia.Controls;
using TimeViewer.ViewModels;

namespace TimeViewer.Views;

public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();

        // The grid's selection is not bindable; hand the view model a snapshot on every change
        ProcessGrid.SelectionChanged += (_, _) =>
        {
            if (DataContext is SettingsViewModel vm)
                vm.SelectedRows = ProcessGrid.SelectedItems.OfType<ProcessRow>().ToList();
        };
    }
}
