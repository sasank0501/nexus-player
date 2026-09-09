using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace MpvFrontend;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // A media player should not vanish mid-film because one handler threw.
        // Before this, an unhandled exception killed the process with nothing in
        // the log at all — the crash on the queue's Add button left only a
        // Windows Error Reporting entry, and the app's own log simply stopped.
        DispatcherUnhandledException += OnDispatcherException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        base.OnStartup(e);
    }

    private void OnDispatcherException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.UserError("Something went wrong: " + e.Exception.Message, e.Exception);
        // Keep running. Playback lives in mpv and is unaffected by a UI fault,
        // so tearing the window down loses the user's place for no reason.
        e.Handled = true;
    }

    private void OnDomainException(object sender, UnhandledExceptionEventArgs e)
    {
        // Cannot be recovered from, but it must not disappear unrecorded.
        Log.Error("Fatal unhandled exception", e.ExceptionObject as Exception);
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        Log.Error("Unobserved task exception", e.Exception);
        e.SetObserved();
    }
}
