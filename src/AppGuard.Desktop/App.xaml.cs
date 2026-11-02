using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using AppGuard.Infrastructure.Persistence;
using AppGuard.Core.Interfaces;
using AppGuard.Infrastructure.Logging;
using AppGuard.Infrastructure.Monitoring;
using AppGuard.Infrastructure.Windows;
using AppGuard.Core.Services;
using AppGuard.Core.Policies;
using System.Windows;
namespace AppGuard.Desktop;

public partial class App : System.Windows.Application
{
    private IHost? _host;
    public async Task ShutdownHostAsync()
    {
        var host = _host;
        if (host is null) return;
        await host.StopAsync(TimeSpan.FromSeconds(5));
        if (host is IAsyncDisposable asyncDisposable) await asyncDisposable.DisposeAsync();
        else host.Dispose();
        _host = null;
    }
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        try
        {
            _host = Host.CreateDefaultBuilder()
                .ConfigureServices(services =>
                {
                    services.AddAppGuardMonitoring();
                    services.AddSingleton<MainWindow>();
                })
                .Build();
            await _host.StartAsync();
            var window = _host.Services.GetRequiredService<MainWindow>();
            MainWindow = window;
            window.Show();
        }
        catch (Exception)
        {
            System.Windows.MessageBox.Show("AppGuard could not initialize. No monitoring was started. / Не удалось запустить AppGuard. Мониторинг не запускался.", "AppGuard", MessageBoxButton.OK, MessageBoxImage.Error);
            if (_host is not null) { try { if (_host is IAsyncDisposable asyncDisposable) await asyncDisposable.DisposeAsync(); else _host.Dispose(); } catch (Exception) { } _host = null; }
            Shutdown(-1);
        }
    }
    protected override async void OnExit(ExitEventArgs e)
    {
        if (_host is not null)
        {
            try { await ShutdownHostAsync(); } catch (Exception) { }
        }
        base.OnExit(e);
    }
}
