using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;

namespace Pinscreen2.App;

/// <summary>
/// Detects and records UI-thread stalls.
///
/// "Pinscreen2.App is not responding" is Windows' observation, not a diagnosis:
/// it names no cause and leaves nothing behind. This pings the dispatcher from a
/// background thread and logs when a ping goes unanswered, so a stall has a
/// start time, an end time and a duration in app.log rather than being
/// reconstructed from a photograph of a frozen clock.
///
/// It cannot unblock the UI thread -- nothing can, from outside. What it can do
/// is make the next hang evidence instead of a mystery.
/// </summary>
public sealed class UiWatchdog : IDisposable
{
    private readonly TimeSpan _interval;
    private readonly TimeSpan _stallThreshold;
    private readonly Action<Action> _post;
    private readonly CancellationTokenSource _cts = new();

    /// <summary>Longest stall observed, for reporting to the dashboard.</summary>
    public double WorstStallSeconds { get; private set; }
    public int StallCount { get; private set; }
    public DateTimeOffset? LastStallAt { get; private set; }

    /// <param name="post">
    /// How to enqueue work on the thread being watched. Injectable so the
    /// detection logic can be tested against a controllable thread rather than
    /// requiring a live windowing system -- the first attempt at testing this
    /// against a non-pumping Avalonia dispatcher reported a permanent stall,
    /// which was the harness being wrong, not the watchdog.
    /// Normal priority, not Background: a stall should mean "blocked", and a
    /// Background-priority ping can sit behind legitimate render work.
    /// </param>
    public UiWatchdog(TimeSpan? stallThreshold = null, TimeSpan? interval = null, Action<Action>? post = null)
    {
        _stallThreshold = stallThreshold ?? TimeSpan.FromSeconds(15);
        _interval = interval ?? TimeSpan.FromSeconds(5);
        _post = post ?? (a => Dispatcher.UIThread.Post(a, DispatcherPriority.Default));
    }

    public void Start() => _ = Task.Run(LoopAsync);

    private async Task LoopAsync()
    {
        var ct = _cts.Token;
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(_interval, ct); }
            catch { break; }

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var acked = new TaskCompletionSource();
            try { _post(() => acked.TrySetResult()); }
            catch { continue; }

            var completed = await Task.WhenAny(acked.Task, Task.Delay(_stallThreshold, ct));
            if (completed != acked.Task)
            {
                // Still blocked. Wait it out so the log records how long it
                // actually lasted, not merely that it exceeded the threshold.
                var stallStart = DateTimeOffset.Now;
                Console.WriteLine($"UI thread unresponsive for more than {_stallThreshold.TotalSeconds:0}s — still waiting…");
                try { await acked.Task.WaitAsync(ct); }
                catch { break; }

                sw.Stop();
                StallCount++;
                LastStallAt = stallStart;
                var seconds = sw.Elapsed.TotalSeconds;
                if (seconds > WorstStallSeconds) WorstStallSeconds = seconds;
                Console.WriteLine($"UI thread recovered after {seconds:0.0}s (stall #{StallCount}, started {stallStart:HH:mm:ss})");
            }
        }
    }

    public void Dispose()
    {
        try { _cts.Cancel(); } catch { }
        try { _cts.Dispose(); } catch { }
    }
}
