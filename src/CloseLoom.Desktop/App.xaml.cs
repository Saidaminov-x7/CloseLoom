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
using System.Security.Principal;
namespace CloseLoom.Desktop;

public partial class App : System.Windows.Application
{
    private IHost? _host;
    private Mutex? _instanceMutex;
    private EventWaitHandle? _activationEvent;
    private CancellationTokenSource? _activationCancellation;
    private Task? _activationTask;
    internal static bool StartInBackground { get; private set; }
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
            StartInBackground = e.Args.Any(argument => string.Equals(argument, "--background", StringComparison.OrdinalIgnoreCase));
            var userSid = WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName;
            _instanceMutex = new Mutex(initiallyOwned: true, $"Local\\CloseLoom-{userSid}", out var createdNew);
            if (!createdNew)
            {
                try
                {
                    using var signal = EventWaitHandle.OpenExisting($"Local\\CloseLoom-Show-{userSid}");
                    signal.Set();
                }
                catch (WaitHandleCannotBeOpenedException) { }
                catch (UnauthorizedAccessException) { }
                _instanceMutex.Dispose();
                _instanceMutex = null;
                Shutdown();
                return;
            }
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
            if (StartInBackground)
            {
                window.Opacity = 0;
                window.ShowInTaskbar = false;
                window.ShowActivated = false;
            }
            window.Show();
            _activationEvent = new EventWaitHandle(false, EventResetMode.AutoReset, $"Local\\CloseLoom-Show-{userSid}");
            _activationCancellation = new CancellationTokenSource();
            var cancellationToken = _activationCancellation.Token;
            var activationEvent = _activationEvent;
            _activationTask = Task.Run(() =>
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    if (activationEvent.WaitOne(250) && !cancellationToken.IsCancellationRequested)
                    {
                        Dispatcher.BeginInvoke(new Action(window.ShowFromTray));
                    }
                }
            }, cancellationToken);
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
        _activationCancellation?.Cancel();
        _activationEvent?.Set();
        if (_activationTask is not null)
        {
            try { await _activationTask; } catch (OperationCanceledException) { }
        }
        _activationEvent?.Dispose();
        _activationCancellation?.Dispose();
        _activationEvent = null;
        _activationCancellation = null;
        _activationTask = null;
        if (_host is not null)
        {
            try { await ShutdownHostAsync(); } catch (Exception) { }
        }
        _instanceMutex?.ReleaseMutex();
        _instanceMutex?.Dispose();
        _instanceMutex = null;
        base.OnExit(e);
    }
}
