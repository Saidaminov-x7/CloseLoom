using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using AppGuard.Infrastructure.Persistence;
using AppGuard.Core.Interfaces;
using System.Windows;
namespace AppGuard.Desktop;

public partial class App : System.Windows.Application
{
    private IHost? _host;
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        try
        {
            _host = Host.CreateDefaultBuilder()
                .ConfigureServices(services => { services.AddSingleton<IUserSettingsStore>(_ => new JsonUserSettingsStore()); services.AddSingleton<JsonApplicationAllowlist>(); services.AddSingleton<IApplicationStore>(sp => sp.GetRequiredService<JsonApplicationAllowlist>()); services.AddSingleton<AppGuard.Core.Interfaces.IAllowlist>(sp => sp.GetRequiredService<JsonApplicationAllowlist>()); services.AddSingleton<MainWindow>(); })
                .Build();
            await _host.StartAsync();
            var window = _host.Services.GetRequiredService<MainWindow>();
            MainWindow = window;
            window.Show();
        }
        catch (Exception)
        {
            System.Windows.MessageBox.Show("AppGuard could not initialize. No monitoring was started.", "AppGuard", MessageBoxButton.OK, MessageBoxImage.Error);
            if (_host is not null) { _host.Dispose(); _host = null; }
            Shutdown(-1);
        }
    }
    protected override async void OnExit(ExitEventArgs e)
    {
        if (_host is not null) { await _host.StopAsync(TimeSpan.FromSeconds(5)); _host.Dispose(); }
        base.OnExit(e);
    }
}
