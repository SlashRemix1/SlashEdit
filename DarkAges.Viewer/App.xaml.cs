using System.Text;
using System.Windows;
using System.Windows.Threading;

namespace DarkAges.Viewer;

public partial class App : System.Windows.Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        if (IsCancellation(e.Exception))
        {
            e.Handled = true;
            return;
        }

        e.Handled = true;
        System.Windows.MessageBox.Show(Describe(e.Exception), AppInfo.Name);
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        e.SetObserved();
        if (!IsCancellation(e.Exception))
        {
            Current?.Dispatcher.BeginInvoke(() =>
                System.Windows.MessageBox.Show(Describe(e.Exception), AppInfo.Name));
        }
    }

    private static void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex && !IsCancellation(ex))
        {
            System.Windows.MessageBox.Show(Describe(ex), AppInfo.Name);
        }
    }

    private static bool IsCancellation(Exception ex) =>
        ex is OperationCanceledException or TaskCanceledException
        || (ex is AggregateException aggregate && aggregate.Flatten().InnerExceptions.All(IsCancellation));

    private static string Describe(Exception ex)
    {
        Exception inner = ex is AggregateException aggregate
            ? aggregate.Flatten().InnerExceptions.FirstOrDefault() ?? ex
            : ex;
        var text = new StringBuilder(inner.Message);
        if (inner.InnerException is not null && inner.InnerException.Message != inner.Message)
        {
            text.AppendLine().Append(inner.InnerException.Message);
        }

        return text.ToString();
    }
}
