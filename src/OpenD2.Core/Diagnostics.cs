using System.Text.Json;

namespace OpenD2.Core;

// Main-thread owned. One bounded frame window; no per-frame collection allocations.
public sealed class FrameMetrics
{
	private readonly double[] samples = new double[600];
	private int cursor;
	private int count;
	public void Record(double seconds)
	{
		if (!double.IsFinite(seconds) || seconds < 0) throw new ArgumentOutOfRangeException(nameof(seconds));
		samples[cursor] = seconds * 1000;
		cursor = (cursor + 1) % samples.Length;
		count = Math.Min(count + 1, samples.Length);
	}
	public double P95Milliseconds()
	{
		if (count == 0) return 0;
		var sorted = samples.AsSpan(0, count).ToArray();
		Array.Sort(sorted);
		return sorted[(int)Math.Ceiling(count * 0.95) - 1];
	}
}

public sealed class SessionLog : IDisposable
{
	private readonly StreamWriter writer;
	public SessionLog(string directory)
	{
		Directory.CreateDirectory(directory);
		foreach (var old in new DirectoryInfo(directory).GetFiles("session-*.jsonl")
			.OrderByDescending(f => f.LastWriteTimeUtc).Skip(9)) old.Delete();
		writer = new StreamWriter(Path.Combine(directory, $"session-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.jsonl"));
		writer.AutoFlush = true;
	}
	public void Write(string eventName, string message) => writer.WriteLine(JsonSerializer.Serialize(new
	{
		timestamp = DateTimeOffset.UtcNow, eventName, message
	}));
	public void Dispose() => writer.Dispose();
}
