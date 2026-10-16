using System.Diagnostics;
using System.Net.WebSockets;
using OpenD2.Online;
using OpenD2.Server;

// Follow-ups to OD-3/OD-5: bounded child shutdown and accurate room stream close causes.
internal static class ShutdownContracts
{
    public static async Task Run()
    {
        Task receive = Task.CompletedTask;
        Check(RoomStream.ShutdownCause(receive, receive, false) == "client closed", "normal client close is not reported as cancellation");
        Check(RoomStream.ShutdownCause(Task.FromResult(0), receive, false) == "send completed", "completed send names the send side");
        Check(RoomStream.ShutdownCause(Task.FromCanceled(new CancellationToken(true)), receive, true) == "request aborted", "cancelled task with aborted request reports request aborted");
        var faulted = Task.FromException(new WebSocketException(WebSocketError.ConnectionClosedPrematurely));
        Check(RoomStream.ShutdownCause(faulted, faulted, false) == "receive socket error ConnectionClosedPrematurely", "socket fault reports its error code");
        Check(RoomStream.ShutdownCause(null, receive, false) == "unknown", "missing first task is unknown");

        Check(PersonalHost.KillTimeout <= TimeSpan.FromSeconds(2), "post-kill wait is capped at 2 seconds");
        if (OperatingSystem.IsWindows()) { Console.WriteLine("SHUTDOWN SKIP child wait on Windows"); return; }
        using var child = Process.Start(new ProcessStartInfo("sleep", "30") { UseShellExecute = false })!;
        var watch = Stopwatch.StartNew();
        Check(!await PersonalHost.WaitForExit(child, TimeSpan.FromMilliseconds(200)) && watch.ElapsedMilliseconds < 2000, "bounded wait returns false for a live child");
        child.Kill();
        Check(await PersonalHost.WaitForExit(child, TimeSpan.FromSeconds(5)), "bounded wait returns true once the child exits");
        Console.WriteLine("SHUTDOWN CONTRACTS PASS");
    }

    private static void Check(bool result, string message)
    {
        if (!result) throw new Exception("SHUTDOWN FAIL " + message);
        Console.WriteLine("SHUTDOWN PASS " + message);
    }
}
