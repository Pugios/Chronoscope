using TimeViewer;

namespace TimeViewer
{
    public partial class AppShell : Shell
    {
        public AppShell()
        {
            InitializeComponent();
            Routing.RegisterRoute(nameof(SettingsPage), typeof(SettingsPage));
            Routing.RegisterRoute(nameof(ExplorerSettingsPage), typeof(ExplorerSettingsPage));
            Routing.RegisterRoute(nameof(StatisticsPage), typeof(StatisticsPage));
        }
    }
}
