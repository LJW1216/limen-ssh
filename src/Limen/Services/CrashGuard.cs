using System.Windows;
using System.Windows.Threading;

namespace Limen;

/// Keeps one unexpected exception from closing the window — and with it every
/// SSH session the user has open. The fault is reported instead, and the app
/// carries on.
public static class CrashGuard
{
    public static IDisposable Install(Application app, Action<Exception> report)
    {
        var reporting = false;

        void OnDispatcher(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            e.Handled = true;
            // A fault that fires on every layout pass would otherwise stack
            // dialogs faster than anyone could close them.
            if (reporting) return;
            reporting = true;
            try { report(e.Exception); }
            finally { reporting = false; }
        }

        // A fire-and-forget task that faulted must not surface later as a
        // crash from the finalizer thread.
        static void OnUnobserved(object? sender, UnobservedTaskExceptionEventArgs e) => e.SetObserved();

        app.DispatcherUnhandledException += OnDispatcher;
        TaskScheduler.UnobservedTaskException += OnUnobserved;
        return new Subscription(() =>
        {
            app.DispatcherUnhandledException -= OnDispatcher;
            TaskScheduler.UnobservedTaskException -= OnUnobserved;
        });
    }

    private sealed class Subscription(Action release) : IDisposable
    {
        public void Dispose() => release();
    }
}
