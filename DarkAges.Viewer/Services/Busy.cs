using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace DarkAges.Viewer.Services;

public static class Busy
{
    private static long _lastPaint;

    public static void Show(FrameworkElement? host, string message, bool determinate = false)
    {
        Overlay(host)?.ShowBusy(message, determinate);
        Mouse.OverrideCursor = System.Windows.Input.Cursors.Wait;
        Flush(host);
    }

    public static void Message(FrameworkElement? host, string message)
    {
        Post(host, () => Overlay(host)?.SetMessage(message));
    }

    public static void Progress(FrameworkElement? host, int done, int total, string? message = null)
    {
        Post(host, () =>
        {
            Overlay(host)?.SetProgress(done, total, message);
            PaintIfDue(host);
        });
    }

    public static IProgress<(int Done, int Total, string Name)> Tracker(FrameworkElement? host, string label) =>
        new CoalescedProgress<(int Done, int Total, string Name)>(host, p =>
            Progress(host, p.Done, p.Total, $"{label} {p.Done}/{p.Total}"));

    public static IProgress<int> Count(FrameworkElement? host, int total, string label) =>
        new CoalescedProgress<int>(host, n =>
            Progress(host, n, total, $"{label} {n}/{total}"));

    public static void Hide(FrameworkElement? host)
    {
        BusyOverlay? overlay = Overlay(host);
        overlay?.HideBusy();
        if (overlay is not { IsBusy: true })
        {
            Mouse.OverrideCursor = null;
        }
    }

    public static void Run(FrameworkElement? host, string message, Action work)
    {
        Show(host, message);
        try
        {
            work();
        }
        finally
        {
            Hide(host);
        }
    }

    /// <summary>
    /// Runs CPU/IO on a worker while the UI dispatcher keeps pumping so the spinner can move.
    /// </summary>
    public static T Compute<T>(
        FrameworkElement? host,
        string message,
        Func<IProgress<(int Done, int Total, string Name)>?, T> work)
    {
        Show(host, message, determinate: true);
        try
        {
            IProgress<(int Done, int Total, string Name)> progress = Tracker(host, TrimLabel(message));
            return Pump(() => work(progress));
        }
        finally
        {
            Hide(host);
        }
    }

    public static T Pump<T>(Func<T> work)
    {
        T result = default!;
        Exception? error = null;
        var frame = new DispatcherFrame();
        _ = Task.Run(() =>
        {
            try
            {
                result = work();
            }
            catch (Exception ex)
            {
                error = ex;
            }
            finally
            {
                frame.Continue = false;
            }
        });
        Dispatcher.PushFrame(frame);
        if (error is not null)
        {
            ExceptionDispatchInfo.Capture(error).Throw();
        }

        return result;
    }

    public static async Task RunAsync(FrameworkElement? host, string message, Func<Task> work)
    {
        Show(host, message);
        try
        {
            await Dispatcher.Yield(DispatcherPriority.Loaded);
            await Dispatcher.Yield(DispatcherPriority.Render);
            await work();
        }
        finally
        {
            Hide(host);
        }
    }

    public static void Flush(FrameworkElement? host)
    {
        host?.UpdateLayout();
        Dispatcher d = host?.Dispatcher ?? Dispatcher.CurrentDispatcher;
        if (d.CheckAccess())
        {
            d.Invoke(() => { }, DispatcherPriority.Render);
        }
    }

    private static void PaintIfDue(FrameworkElement? host)
    {
        Dispatcher d = host?.Dispatcher ?? Dispatcher.CurrentDispatcher;
        if (!d.CheckAccess())
        {
            return;
        }

        long now = Stopwatch.GetTimestamp();
        if (_lastPaint != 0 && Stopwatch.GetElapsedTime(_lastPaint).TotalMilliseconds < 40)
        {
            host?.InvalidateVisual();
            return;
        }

        _lastPaint = now;
        Flush(host);
    }

    private static void Post(FrameworkElement? host, Action apply)
    {
        Dispatcher d = host?.Dispatcher ?? Dispatcher.CurrentDispatcher;
        if (d.CheckAccess())
        {
            apply();
            return;
        }

        try
        {
            if (d.HasShutdownStarted || d.HasShutdownFinished)
            {
                return;
            }

            d.BeginInvoke(apply, DispatcherPriority.Background);
        }
        catch (TaskCanceledException)
        {
        }
        catch (InvalidOperationException)
        {
        }
    }

    private static string TrimLabel(string message)
    {
        string text = message.Trim();
        return text.TrimEnd('.', '…', ' ');
    }

    private static BusyOverlay? Overlay(FrameworkElement? host)
    {
        if (host is null)
        {
            return null;
        }

        if (host is Window window && window.FindName("BusyHost") is BusyOverlay named)
        {
            return named;
        }

        if (host.FindName("BusyHost") is BusyOverlay child)
        {
            return child;
        }

        return Window.GetWindow(host)?.FindName("BusyHost") as BusyOverlay;
    }

    private sealed class CoalescedProgress<T>(FrameworkElement? host, Action<T> apply) : IProgress<T>
    {
        private readonly Dispatcher _dispatcher = host?.Dispatcher ?? Dispatcher.CurrentDispatcher;
        private readonly object _gate = new();
        private T? _latest;
        private int _scheduled;

        public void Report(T value)
        {
            lock (_gate)
            {
                _latest = value;
            }

            if (_dispatcher.CheckAccess())
            {
                apply(value);
                return;
            }

            if (Interlocked.Exchange(ref _scheduled, 1) == 1)
            {
                return;
            }

            try
            {
                if (_dispatcher.HasShutdownStarted || _dispatcher.HasShutdownFinished)
                {
                    Interlocked.Exchange(ref _scheduled, 0);
                    return;
                }

                _dispatcher.BeginInvoke(Drain, DispatcherPriority.Background);
            }
            catch (TaskCanceledException)
            {
                Interlocked.Exchange(ref _scheduled, 0);
            }
            catch (InvalidOperationException)
            {
                Interlocked.Exchange(ref _scheduled, 0);
            }
        }

        private void Drain()
        {
            T? seen;
            lock (_gate)
            {
                seen = _latest;
            }

            try
            {
                apply(seen!);
            }
            finally
            {
                Interlocked.Exchange(ref _scheduled, 0);
                T? latest;
                lock (_gate)
                {
                    latest = _latest;
                }

                if (!Equals(seen, latest))
                {
                    Report(latest!);
                }
            }
        }
    }
}
