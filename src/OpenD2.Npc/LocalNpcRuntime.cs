using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;
using System.Security.Cryptography;

namespace OpenD2.Npc;

// Owns one loopback process; no shell, external server URL, API account or automatic model download.
public sealed class LocalNpcRuntime : INpcModel, IDisposable
{
	private readonly SemaphoreSlim lifecycle = new(1, 1);
	private readonly CancellationTokenSource lifetime = new();
	private readonly ScriptedNpcModel scripted = new();
	private Process? process;
	private LlamaNpcModel? model;
	private volatile bool disposed, enabled;
	public bool Enabled => enabled;
	public bool IsRunning { get { try { return Volatile.Read(ref model) is not null && Volatile.Read(ref process) is { HasExited: false }; } catch (InvalidOperationException) { return false; } } }
	public long WorkingSetBytes { get { try { var child = Volatile.Read(ref process); child?.Refresh(); return child?.WorkingSet64 ?? 0; } catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception) { return 0; } } }
	public static string RuntimeId => (OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "osx" : "linux") + "-" + RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();
	public static bool IsHardwareSupported => RuntimeId == "osx-arm64" ||
		(RuntimeId is "win-x64" or "linux-x64" && Avx2.IsSupported && Fma.IsSupported && (X86Base.CpuId(1, 0).Ecx & (1 << 29)) != 0);
	public static string ExecutableIn(string applicationDirectory) => Path.Combine(applicationDirectory, "npc-runtime", RuntimeId, OperatingSystem.IsWindows() ? "llama-server.exe" : "llama-server");
	public static string BundledExecutable => ExecutableIn(AppContext.BaseDirectory);
	private static void EnsureExecutable(string executable)
	{
		if (!OperatingSystem.IsWindows() && (File.GetUnixFileMode(executable) & UnixFileMode.UserExecute) == 0)
			File.SetUnixFileMode(executable, File.GetUnixFileMode(executable) | UnixFileMode.UserExecute);
	}
	public static async Task VerifyBundledAsync(CancellationToken token, string? executable = null)
	{
		if (!IsHardwareSupported) throw new PlatformNotSupportedException("Unsupported optional NPC CPU backend.");
		executable ??= BundledExecutable; EnsureExecutable(executable);
		using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(10));
		var info = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
		info.ArgumentList.Add("--version");
		using var child = new Process { StartInfo = info };
		try
		{
			if (!child.Start()) throw new IOException("Unable to verify the bundled NPC runtime.");
			_ = Drain(child.StandardOutput); _ = Drain(child.StandardError);
			await child.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
			if (child.ExitCode != 0) throw new IOException("Bundled NPC runtime failed its version probe.");
		}
		finally { Kill(child); }
	}
	public Task StartAsync(string executable, string modelPath, CancellationToken token) => Task.Run(async () =>
	{
		using var startup = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token); startup.CancelAfter(TimeSpan.FromSeconds(45));
		await lifecycle.WaitAsync(startup.Token).ConfigureAwait(false);
		try
		{
			ObjectDisposedException.ThrowIf(disposed, this); await StopCore().ConfigureAwait(false); enabled = true;
			if (!IsHardwareSupported) throw new PlatformNotSupportedException("Local AI requires Apple Silicon or x64 AVX2/FMA/F16C. Scripted dialogue remains available.");
			if (!File.Exists(executable) || !File.Exists(modelPath)) throw new FileNotFoundException("Bundled NPC runtime or verified model is missing.");
			EnsureExecutable(executable);
			using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); int port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
			string key = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
			var info = new ProcessStartInfo(Path.GetFullPath(executable)) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
			foreach (string name in info.Environment.Keys.Where(k => k.StartsWith("LLAMA_", StringComparison.OrdinalIgnoreCase)).ToArray()) info.Environment.Remove(name);
			info.Environment["LLAMA_API_KEY"] = key;
			foreach (string argument in new[] { "--model", Path.GetFullPath(modelPath), "--host", "127.0.0.1", "--port", port.ToString(System.Globalization.CultureInfo.InvariantCulture),
				"--alias", "opend2-npc", "--ctx-size", "4096", "--parallel", "1", "--threads", Math.Clamp(Environment.ProcessorCount / 2, 1, 4).ToString(),
				"--threads-http", "2", "--n-gpu-layers", "0", "--predict", "96", "--offline", "--no-webui", "--no-webui-mcp-proxy", "--log-disable",
				"--chat-template-kwargs", "{\"enable_thinking\":false}", "--reasoning-budget", "0" }) info.ArgumentList.Add(argument);
			var child = new Process { StartInfo = info }; Volatile.Write(ref process, child);
			if (!child.Start()) throw new IOException("Unable to start the local NPC runtime.");
			_ = Drain(child.StandardOutput); _ = Drain(child.StandardError);
			startup.Token.ThrowIfCancellationRequested();
			var adapter = new LlamaNpcModel(new Uri($"http://127.0.0.1:{port}/"), key); Volatile.Write(ref model, adapter);
			while (true)
			{
				startup.Token.ThrowIfCancellationRequested();
				if (child.HasExited) throw new IOException($"Local NPC runtime exited during startup (code {child.ExitCode}).");
				try
				{
					using var probe = CancellationTokenSource.CreateLinkedTokenSource(startup.Token); probe.CancelAfter(TimeSpan.FromSeconds(2));
					if (await adapter.ReadyAsync(probe.Token).ConfigureAwait(false)) break;
				}
				catch (HttpRequestException) { }
				catch (OperationCanceledException) when (!startup.IsCancellationRequested) { }
				await Task.Delay(100, startup.Token).ConfigureAwait(false);
			}
		}
		catch { await StopCore().ConfigureAwait(false); throw; }
		finally { lifecycle.Release(); }
	}, token);
	public Task<string> RespondAsync(NpcRequest request, CancellationToken token)
	{
		ObjectDisposedException.ThrowIf(disposed, this);
		if (!enabled) return scripted.RespondAsync(request, token);
		var adapter = Volatile.Read(ref model);
		if (adapter is null || !IsRunning) throw new IOException("Local NPC runtime is unavailable.");
		return adapter.RespondAsync(request, token);
	}
	public async Task StopAsync()
	{
		await lifecycle.WaitAsync().ConfigureAwait(false);
		try { await StopCore().ConfigureAwait(false); } finally { lifecycle.Release(); }
	}
	private async Task StopCore()
	{
		enabled = false; Interlocked.Exchange(ref model, null)?.Dispose();
		if (Volatile.Read(ref process) is not { } child) return;
		bool stopped = false;
		try { Kill(child); await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); stopped = true; }
		catch (InvalidOperationException) { stopped = true; } // Start may have failed before assigning an OS handle.
		catch { enabled = true; throw; } // Retain ownership so Stop/restart can retry an OS-level termination failure.
		finally { if (stopped) { Interlocked.CompareExchange(ref process, null, child); child.Dispose(); } }
	}
	private static void Kill(Process? child)
	{
		try { if (child is { HasExited: false }) child.Kill(entireProcessTree: true); }
		catch (InvalidOperationException) { }
	}
	private static async Task Drain(StreamReader reader)
	{
		try { char[] buffer = new char[1024]; while (await reader.ReadAsync(buffer).ConfigureAwait(false) != 0) { } }
		catch (Exception error) when (error is IOException or ObjectDisposedException) { }
	}
	public void Dispose()
	{
		if (disposed) return; disposed = true; _ = lifetime.CancelAsync();
		try { Kill(Volatile.Read(ref process)); } catch (System.ComponentModel.Win32Exception) { } // Async cleanup retries; do not throw through the engine exit callback.
		_ = FinishDisposal();
	}
	private async Task FinishDisposal()
	{
		try { await StopAsync().ConfigureAwait(false); }
		catch (Exception error) when (error is IOException or TimeoutException or System.ComponentModel.Win32Exception) { }
		finally { lifetime.Dispose(); }
	}
}
