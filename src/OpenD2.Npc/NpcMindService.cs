namespace OpenD2.Npc;

// Single-owner Request/Poll/Invalidate/Dispose; the adapter runs on a worker.
// One physical worker slot, no queued requests. Even ignored cancellation cannot grow the queue.
public sealed class NpcMindService : IDisposable
{
	private sealed record Completion(string? Json, bool Failed);
	private sealed class Pending(NpcRequest request, long started)
	{
		public readonly NpcRequest Request = request;
		public readonly long Started = started;
		public readonly CancellationTokenSource Cancellation = new();
		public Task<Completion> Worker = null!;
		public Task Cancelled = Task.CompletedTask;
		public bool Suppressed, Delivered, CancelRequested;
	}
	private readonly INpcModel model;
	private readonly TimeProvider time;
	private readonly TimeSpan deadline;
	private Pending? pending;
	private long nextId;
	private bool disposed;
	public long Generation { get; private set; } = 1;
	public bool IsBusy => pending is not null;
	public NpcMindService(INpcModel model, TimeProvider? time = null, TimeSpan? deadline = null)
	{
		this.model = model ?? throw new ArgumentNullException(nameof(model));
		this.time = time ?? TimeProvider.System; this.deadline = deadline ?? TimeSpan.FromSeconds(8);
		if (this.deadline <= TimeSpan.Zero || this.deadline > TimeSpan.FromMinutes(1)) throw new ArgumentOutOfRangeException(nameof(deadline));
	}
	public NpcStart Request(NpcFacts facts, string? input)
	{
		ObjectDisposedException.ThrowIf(disposed, this);
		Drain();
		if (pending is not null) return NpcStart.Busy;
		if (!facts.CanTalk) return NpcStart.Unavailable;
		if (string.IsNullOrWhiteSpace(input) || input.Length > NpcDecisionGate.MaxInputChars || input.Any(char.IsControl)) return NpcStart.InvalidInput;
		var work = new Pending(new(checked(++nextId), Generation, facts, input.Trim()), time.GetTimestamp());
		pending = work;
		work.Worker = Task.Run(async () =>
		{
			try { return new Completion(await model.RespondAsync(work.Request, work.Cancellation.Token).ConfigureAwait(false), false); }
			catch (Exception) { return new Completion(null, true); } // No unobserved faults or raw input in logs.
		});
		return NpcStart.Accepted;
	}
	public NpcResult? Poll(NpcFacts current)
	{
		ObjectDisposedException.ThrowIf(disposed, this);
		if (pending is not { } work) return null;
		NpcResult? result = null;
		if (!work.Delivered && !work.Suppressed)
		{
			if (!NpcDecisionGate.IsCurrent(work.Request, Generation, current)) { work.Suppressed = true; Cancel(work); }
			else if (time.GetElapsedTime(work.Started) >= deadline)
			{
				work.Delivered = true; Cancel(work);
				result = Fallback(work, NpcOutcome.Timeout, current);
			}
			else if (work.Worker.IsCompleted)
			{
				work.Delivered = true; var output = work.Worker.GetAwaiter().GetResult();
				result = output.Failed ? Fallback(work, NpcOutcome.ModelFailure, current) :
					NpcDecisionGate.TryDecode(output.Json, current, out var reply) ? new(work.Request, NpcOutcome.Answer, reply!) :
					Fallback(work, NpcOutcome.InvalidResponse, current);
			}
		}
		Drain(); return result;
	}
	private static NpcResult Fallback(Pending work, NpcOutcome outcome, NpcFacts current) =>
		new(work.Request, outcome, NpcDecisionGate.Render(NpcIntent.QuestStatus, current));
	public void Invalidate()
	{
		ObjectDisposedException.ThrowIf(disposed, this);
		Generation = checked(Generation + 1);
		if (pending is { } work) { work.Suppressed = true; Cancel(work); }
		Drain();
	}
	private static void Cancel(Pending work)
	{
		if (work.CancelRequested) return;
		work.CancelRequested = true; work.Cancelled = CancelSafely(work.Cancellation);
	}
	private static async Task CancelSafely(CancellationTokenSource source)
	{
		try { await source.CancelAsync().ConfigureAwait(false); }
		catch (Exception) { /* A broken adapter cancellation callback must not stop the game. */ }
	}
	private void Drain()
	{
		if (pending is { } work && (work.Delivered || work.Suppressed) && work.Worker.IsCompleted && work.Cancelled.IsCompleted)
		{ work.Cancellation.Dispose(); pending = null; }
	}
	public void Dispose()
	{
		if (disposed) return;
		Invalidate(); disposed = true;
		if (pending is { } work) { pending = null; _ = ReleaseAsync(work); }
	}
	private static async Task ReleaseAsync(Pending work)
	{
		await work.Worker.ConfigureAwait(false); await work.Cancelled.ConfigureAwait(false); work.Cancellation.Dispose();
	}
}
