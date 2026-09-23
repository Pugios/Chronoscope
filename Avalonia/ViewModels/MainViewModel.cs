using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace TimeViewer.ViewModels;

public partial class MainViewModel : ViewModelBase
{
    [ObservableProperty]
    public partial string Greeting { get; set; } = "Welcome to Avalonia!";

    [ObservableProperty]
    public partial int ClickCount { get; set; } = 0;

    [ObservableProperty]
    public partial string ButtonText { get; set; } = "Clicked me 0 times fancily";

    [RelayCommand]
    private void Increment()
    {
        ClickCount++;
        ButtonText = $"Clicked me {ClickCount} times fancily";
    }
}
