using CommunityToolkit.Mvvm.ComponentModel;

namespace TimeViewer.ViewModels;

public abstract class ViewModelBase : ObservableObject
{
    // The MAUI pages' OnAppearing / OnDisappearing. Raised by MainWindowViewModel whenever this
    // view model becomes, or stops being, the one on screen - including on returning to it.
    public virtual Task OnNavigatedToAsync() => Task.CompletedTask;
    public virtual void OnNavigatedFrom() { }
}
