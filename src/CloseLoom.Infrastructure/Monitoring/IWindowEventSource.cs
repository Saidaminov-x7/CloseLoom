namespace CloseLoom.Infrastructure.Monitoring;

/// <summary>Reports native top-level-window visibility/destruction events. Callbacks may arrive on a native hook thread.</summary>
public interface IWindowEventSource : IDisposable
{
    void Start(Action<int?> onWindowChanged, Action<Exception> onError);
    void Stop();
}
