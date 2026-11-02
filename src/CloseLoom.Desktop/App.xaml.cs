using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using CloseLoom.Infrastructure.Persistence;
using CloseLoom.Core.Interfaces;
using CloseLoom.Infrastructure.Logging;
using CloseLoom.Infrastructure.Monitoring;
using CloseLoom.Infrastructure.Windows;
using CloseLoom.Core.Services;
using CloseLoom.Core.Policies;
using System.Windows;
namespace CloseLoom.Desktop;

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
                    services.AddCloseLoomMonitoring();
                    services.AddSingleton<MainWindow>();
                })
                .Build();
            await _host.StartAsync();
            var window = _host.Services.GetRequiredService<MainWindow>();
            MainWindow = window;
            window.Show();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceError("CloseLoom startup failed: {0}", ex.GetType().Name);
            System.Windows.MessageBox.Show("CloseLoom could not initialize. No monitoring started. / Не удалось запустить CloseLoom. Мониторинг не запускался.", "CloseLoom", MessageBoxButton.OK, MessageBoxImage.Error);
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
