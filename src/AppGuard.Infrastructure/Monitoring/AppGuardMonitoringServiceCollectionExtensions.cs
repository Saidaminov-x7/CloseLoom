using AppGuard.Core.Interfaces;
using AppGuard.Core.Policies;
using AppGuard.Core.Services;
using AppGuard.Infrastructure.Logging;
using AppGuard.Infrastructure.Persistence;
using AppGuard.Infrastructure.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AppGuard.Infrastructure.Monitoring;

public static class AppGuardMonitoringServiceCollectionExtensions
{
    /// <summary>Registers the fail-closed monitor and its Windows adapters. Active termination remains disabled.</summary>
    public static IServiceCollection AddAppGuardMonitoring(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<IUserSettingsStore, JsonUserSettingsStore>();
        services.TryAddSingleton<JsonApplicationAllowlist>();
        services.TryAddSingleton<IApplicationStore>(provider => provider.GetRequiredService<JsonApplicationAllowlist>());
        services.TryAddSingleton<IAllowlist>(provider => provider.GetRequiredService<JsonApplicationAllowlist>());
        services.TryAddSingleton<JsonLinesActivityLog>();
        services.TryAddSingleton<CountingActivityLog>(provider => new CountingActivityLog(provider.GetRequiredService<JsonLinesActivityLog>()));
        services.Replace(ServiceDescriptor.Singleton<IActivityLog>(provider => provider.GetRequiredService<CountingActivityLog>()));
        services.TryAddSingleton<IProcessCatalog, WindowsProcessCatalog>();
        services.TryAddSingleton<IProcessTerminator, WindowsProcessTerminator>();
        services.TryAddSingleton<IWindowMonitor, WindowsWindowMonitor>();
        services.TryAddSingleton<IWindowEventSource, WindowsWindowEventSource>();
        services.TryAddSingleton<TerminationPolicy>();
        services.TryAddSingleton<ISafeTerminationService, SafeTerminationService>();
        services.TryAddSingleton<GracePeriodCoordinator>();
        services.TryAddSingleton<IApplicationMonitor, WindowsApplicationMonitor>();
        return services;
    }
}
