using OpenD2.Core;
using OpenD2.Npc;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

if (args.Length < 3 || args[0] is not ("install" or "evaluate"))
{
	Console.Error.WriteLine("install <model-id> <model-directory> | evaluate <model-id> <model-directory> <cases.json> <report.json>"); return 2;
}
var model = NpcModelCatalog.All.SingleOrDefault(m => m.Id == args[1]) ?? throw new ArgumentException("Unknown catalog model.");
using var cancel = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancel.Cancel(); };
using var store = new NpcModelStore(args[2]);
if (args[0] == "install")
{
	Console.WriteLine($"Optional download: {model.Name}, {model.Bytes} bytes, Apache-2.0. Source: {model.DownloadUri}");
	await store.InstallAsync(model, new DownloadProgress(model.Bytes), cancel.Token);
	Console.WriteLine("MODEL VERIFIED: " + model.Id); return 0;
}
if (args.Length != 5) throw new ArgumentException("Evaluation requires cases and output report paths.");
string path = await store.VerifyAsync(model, cancel.Token);
byte[] source = await File.ReadAllBytesAsync(args[3], cancel.Token);
var cases = JsonSerializer.Deserialize<Case[]>(source) ?? throw new InvalidDataException("Missing cases.");
if (cases.Length != 100 || cases.Select(c => c.Id).Distinct().Count() != 100) throw new InvalidDataException("Exactly 100 uniquely identified synthetic cases are required.");
using var runtime = new LocalNpcRuntime();
var startup = Stopwatch.StartNew();
await runtime.StartAsync(LocalNpcRuntime.BundledExecutable, path, cancel.Token);
double loadMs = startup.Elapsed.TotalMilliseconds;
Console.WriteLine($"RUNTIME READY: {model.Id}, load {loadMs:F0} ms");
var rows = new List<Row>();
foreach (var item in cases)
{
	cancel.Token.ThrowIfCancellationRequested();
	var stage = Enum.Parse<QuestStage>(item.Stage);
	var facts = new NpcFacts(new(1), new(10), new(1), "synthetic-npc02", new(stage, stage is QuestStage.Completed or QuestStage.ReadyToTurnIn ? 3 : stage == QuestStage.Active ? 1 : 0, 3), true);
	var watch = Stopwatch.StartNew(); string? intent = null, error = null;
	try
	{
		string json = await runtime.RespondAsync(new(rows.Count + 1, 0, facts, item.Input), cancel.Token);
		if (NpcDecisionGate.TryDecode(json, facts, out var reply)) intent = reply!.Intent.ToString(); else error = "InvalidDecision";
	}
	catch (OperationCanceledException) when (!cancel.IsCancellationRequested) { error = "Timeout"; }
	catch (Exception e) when (e is IOException or HttpRequestException or JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
	{ error = e.GetType().Name; }
	rows.Add(new(item.Id, item.Category, item.Expected, intent, error, Math.Round(watch.Elapsed.TotalMilliseconds, 2), runtime.WorkingSetBytes));
	Console.WriteLine($"{rows.Count}/100 {item.Id}: {intent ?? error} {(intent == item.Expected ? "correct" : "MISS")} {watch.Elapsed.TotalMilliseconds:F0} ms");
}
await runtime.StopAsync();
var durations = rows.Select(r => r.Milliseconds).Order().ToArray();
long peakRss = rows.Max(r => r.WorkingSetBytes);
var report = new
{
	Schema = 1, Utc = DateTimeOffset.UtcNow, Model = model, CasesSha256 = Convert.ToHexStringLower(SHA256.HashData(source)),
	Runtime = "llama.cpp v0.6.0 d81235049384534c167caea52b85a694f6103d14", Backend = "CPU", Architecture = RuntimeInformation.ProcessArchitecture.ToString(),
	Os = RuntimeInformation.OSDescription, LogicalProcessorsVisibleToDotnet = Environment.ProcessorCount, InferenceThreads = Math.Clamp(Environment.ProcessorCount / 2, 1, 4),
	Limitations = "Synthetic intent classification only; host-authored speech; sequential requests; no rendering/GPU/target-user-PC acceptance or freeform factuality measurement.",
	LoadMilliseconds = Math.Round(loadMs, 2), Valid = rows.Count(r => r.Actual != null), Correct = rows.Count(r => r.Actual == r.Expected), Total = rows.Count,
	P50Milliseconds = durations[49], P95Milliseconds = durations[94], MaxMilliseconds = durations[^1],
	FirstRequestMilliseconds = rows[0].Milliseconds, SampledPeakWorkingSetBytes = peakRss > 0 ? (long?)peakRss : null,
	WorkingSetMeasurement = peakRss > 0 ? "Sampled after each response; not a continuous peak" : "Unavailable: this host returned zero for every Process.WorkingSet64 sample",
	Categories = rows.GroupBy(r => r.Category).Select(g => new { Category = g.Key, Correct = g.Count(r => r.Actual == r.Expected), Total = g.Count() }), Rows = rows
};
Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(args[4]))!);
await File.WriteAllTextAsync(args[4], JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }) + "\n", cancel.Token);
Console.WriteLine($"EVALUATION: {report.Correct}/{report.Total} correct, {report.Valid} valid; p95 {report.P95Milliseconds:F0} ms; sampled RSS {(peakRss > 0 ? (peakRss / 1048576.0).ToString("F0") + " MiB" : "unavailable")}");
return 0; // A report records poor quality too; it does not certify acceptance.

internal sealed record Case(string Id, string Category, string Stage, string Input, string Expected);
internal sealed record Row(string Id, string Category, string Expected, string? Actual, string? Error, double Milliseconds, long WorkingSetBytes);
internal sealed class DownloadProgress(long total) : IProgress<long>
{
	private int last = -1;
	public void Report(long bytes) { int percent = (int)(bytes * 100 / total); if (percent / 10 != last) { last = percent / 10; Console.WriteLine($"Download {percent}%"); } }
}
