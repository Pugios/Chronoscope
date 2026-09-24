using CommunityToolkit.Mvvm.ComponentModel;

namespace Chronoscope.ViewModels;

// A page whose view is built once and then kept: leaving it only hides it, and coming back shows
// the same controls again. For the pages whose charts are expensive to build (the day's pie and
// timeline, the Statistics heatmaps) - rebuilding them on every visit is what made opening them
// stall. The other pages are cheap and are meant to start fresh (Discard means discard).
public interface IKeepAlive { }

public abstract class ViewModelBase : ObservableObject
{
    // The MAUI pages' OnAppearing / OnDisappearing. Raised by MainWindowViewModel whenever this
    // view model becomes, or stops being, the one on screen - including on returning to it.
    public virtual Task OnNavigatedToAsync() => Task.CompletedTask;
    public virtual void OnNavigatedFrom() { }
}
