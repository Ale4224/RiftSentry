using System.Diagnostics;
using System.Drawing;
using System.Net.Http;
using System.Windows;
using System.Windows.Forms;
using RiftSentry.Services;
using RiftSentry.ViewModels;
using Application = System.Windows.Application;

namespace RiftSentry;

public partial class App : Application
{
    private HttpClient? _http;
    private MainViewModel? _vm;
    private NotifyIcon? _tray;
    private SettingsWindow? _settingsWindow;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _http = new HttpClient();
        var ddragon = new DataDragonService(_http);
        var assets = new AssetCacheService(_http);
        var live = new LiveClientService();
        var settings = new AppSettingsService();
        var syncClient = new SyncClientService();
        var main = new MainWindow();
        _vm = new MainViewModel(ddragon, assets, live, settings, syncClient, main.Dispatcher);
        main.DataContext = _vm;
        MainWindow = main;
        CreateTrayIcon();
        main.Show();
        main.Hide();
        _ = _vm.InitializeAsync();
    }

    private void CreateTrayIcon()
    {
        var icon = TryLoadTrayIcon();
        var menu = new ContextMenuStrip();
        menu.Items.Add("Settings", null, (_, _) => ShowSettingsWindow());
        menu.Items.Add("Exit", null, (_, _) => Shutdown());

        _tray = new NotifyIcon
        {
            Icon = icon,
            Visible = true,
            Text = "RiftSentry",
            ContextMenuStrip = menu
        };
        _tray.DoubleClick += (_, _) => ShowSettingsWindow();
    }

    private static Icon TryLoadTrayIcon()
    {
        try
        {
            var path = Process.GetCurrentProcess().MainModule?.FileName;
            if (!string.IsNullOrEmpty(path))
            {
                var extracted = Icon.ExtractAssociatedIcon(path);
                if (extracted != null)
                    return extracted;
            }
        }
        catch
        {
        }

        return SystemIcons.Application;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_settingsWindow != null)
        {
            _settingsWindow.Close();
            _settingsWindow = null;
        }

        if (_tray != null)
        {
            _tray.Visible = false;
            _tray.Dispose();
            _tray = null;
        }

        _vm?.Dispose();
        _http?.Dispose();
        base.OnExit(e);
    }

    private void ShowSettingsWindow()
    {
        if (_vm == null)
            return;

        Current.Dispatcher.Invoke(() =>
        {
            if (_settingsWindow == null)
            {
                _settingsWindow = new SettingsWindow
                {
                    DataContext = _vm
                };
                _settingsWindow.Closed += (_, _) => _settingsWindow = null;
            }

            if (!_settingsWindow.IsVisible)
                _settingsWindow.Show();

            if (_settingsWindow.WindowState == WindowState.Minimized)
                _settingsWindow.WindowState = WindowState.Normal;

            _settingsWindow.Activate();
            _settingsWindow.Topmost = true;
            _settingsWindow.Topmost = false;
            _settingsWindow.Focus();
        });
    }
}