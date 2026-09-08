using System;
using System.Threading.Tasks;

namespace MpvFrontend;

// v1 used `async void` on methods that are not event handlers (HandleHotkey,
// ForwardTextToMpv, NudgeVideoSurface). An exception in any of them is
// unhandled and takes the whole app down. These start work deliberately and
// log failures instead.
public static class Fire
{
    public static void AndForget(Task? task, string what)
    {
        if (task == null) return;
        _ = task.ContinueWith(
            t => Log.Error(what + " failed", t.Exception?.GetBaseException()),
            TaskContinuationOptions.OnlyOnFaulted);
    }

    public static void AndForget(Func<Task?> work, string what)
    {
        try { AndForget(work(), what); }
        catch (Exception ex) { Log.Error(what + " failed", ex); }
    }
}
