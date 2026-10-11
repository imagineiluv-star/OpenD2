using System.Diagnostics;

namespace OpenD2.Online;

// Owns only the child it starts. stdin is a private control channel: EOF also
// shuts the server down if its client crashes, without a network shutdown API.
public sealed class PersonalHost : IDisposable
{
    private Process? process;
    private int disposed;
    private readonly SemaphoreSlim gate = new(1);
    public string? Address { get; private set; }
    public bool Running => process is { HasExited: false };
    public async Task Start(string executable, string data, CancellationToken cancellation = default)
    {
        await gate.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed != 0, this);
            if (Running) throw new InvalidOperationException("Personal host is already running.");
            process?.Dispose(); process = null; Address = null;
            executable = Path.GetFullPath(executable); data = Path.GetFullPath(data);
            if (!File.Exists(executable)) throw new FileNotFoundException("Select the extracted OpenD2.Server executable.", executable);
            var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                WorkingDirectory = Path.GetDirectoryName(executable)! };
            foreach (var arg in new[] { "--managed-host", "true", "--mode", "open", "--data", data }) start.ArgumentList.Add(arg);
            var ready = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            var child = new Process { StartInfo = start, EnableRaisingEvents = true };
            child.OutputDataReceived += (_, e) =>
            {
                const string marker = "OPEND2_HOST_READY ";
                if (e.Data?.StartsWith(marker, StringComparison.Ordinal) == true) ready.TrySetResult(e.Data[marker.Length..]);
            };
            // Drain both pipes without retaining account data or unbounded logs.
            child.ErrorDataReceived += (_, _) => { };
            child.Exited += (_, _) => ready.TrySetException(new IOException("Personal host exited. Check the executable, permissions and data folder; it may already be in use."));
            process = child;
            try
            {
                child.Start(); child.BeginOutputReadLine(); child.BeginErrorReadLine();
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
                timeout.CancelAfter(TimeSpan.FromSeconds(20));
                string address = await ready.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
                if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.Scheme != "http" || uri.Host != "127.0.0.1" || uri.Port <= 0 || uri.AbsolutePath != "/")
                    throw new InvalidDataException("Invalid personal host address.");
                using var probe = new OnlineClient(address, mode: "open");
                await probe.CheckServer(timeout.Token).ConfigureAwait(false);
                if (child.HasExited) throw new IOException("Personal host exited during startup.");
                Address = address;
            }
            catch { await StopChild().ConfigureAwait(false); throw; }
        }
        finally { gate.Release(); }
    }
    // False means the bounded graceful stop failed and the owned child was killed.
    public async Task<bool> Stop()
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try { return await StopChild().ConfigureAwait(false); }
        finally { gate.Release(); }
    }
    private async Task<bool> StopChild()
    {
        var child = process; process = null; Address = null;
        if (child is null) return true;
        bool clean = true;
        try
        {
            try
            {
                if (!child.HasExited)
                {
                    child.StandardInput.Close();
                    await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
                }
                clean = child.ExitCode == 0;
            }
            catch (InvalidOperationException) { } // Process.Start failed before a PID existed.
            catch (Exception e) when (e is TimeoutException or IOException)
            {
                clean = false;
                if (!child.HasExited) { child.Kill(entireProcessTree: true); await child.WaitForExitAsync().ConfigureAwait(false); }
            }
        }
        finally { child.Dispose(); }
        return clean;
    }
    public void Dispose() { Interlocked.Exchange(ref disposed, 1); Stop().GetAwaiter().GetResult(); }
}
