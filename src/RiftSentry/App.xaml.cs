using System.Net.Http;
using System.Windows;
using RiftSentry.Services;
using RiftSentry.ViewModels;

namespace RiftSentry;

public partial class App : Application
{
    private HttpClient? _http;
    private MainViewModel? _vm;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _http = new HttpClient();
        var ddragon = new DataDragonService(_http);
        var assets = new AssetCacheService(_http);
        var live = new LiveClientService();
        var main = new MainWindow();
        _vm = new MainViewModel(ddragon, assets, live, main.Dispatcher);
        main.DataContext = _vm;
        MainWindow = main;
        main.Show();
        _ = _vm.InitializeAsync();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _vm?.Dispose();
        _http?.Dispose();
        base.OnExit(e);
    }
}
