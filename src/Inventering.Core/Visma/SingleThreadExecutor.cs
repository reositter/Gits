using System.Collections.Concurrent;

namespace Inventering.Core.Visma;

/// <summary>
/// Kör allt arbete på en och samma dedikerade tråd. ADK är inte trådsäkert och
/// ett öppnat företag är knutet till tråden – därför går alla ADK-anrop härigenom.
/// </summary>
public sealed class SingleThreadExecutor : IDisposable
{
    private readonly BlockingCollection<Action> _queue = new();
    private readonly Thread _thread;

    public SingleThreadExecutor(string name)
    {
        _thread = new Thread(() =>
        {
            foreach (var work in _queue.GetConsumingEnumerable()) work();
        }) { IsBackground = true, Name = name };
        if (OperatingSystem.IsWindows()) _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    public Task<T> RunAsync<T>(Func<T> func)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _queue.Add(() =>
        {
            try { tcs.SetResult(func()); }
            catch (Exception ex) { tcs.SetException(ex); }
        });
        return tcs.Task;
    }

    public void Dispose()
    {
        _queue.CompleteAdding();
        _thread.Join(TimeSpan.FromSeconds(10));
        _queue.Dispose();
    }
}
