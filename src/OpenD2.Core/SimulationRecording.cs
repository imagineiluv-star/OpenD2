namespace OpenD2.Core;

// Bounded diagnostic recording. Rolling the replay window never pauses or resets live gameplay.
public sealed class SimulationRecording
{
	public const int TickWindow = 15000, CommandWindow = 4096;
	private readonly GameSimulation simulation;
	private readonly List<RecordedCommand> commands = new(CommandWindow);
	public IReadOnlyList<RecordedCommand> Commands { get; }
	public SimulationSnapshot Baseline { get; private set; }
	public long WindowsRolled { get; private set; }
	public SimulationRecording(GameSimulation simulation)
	{
		ArgumentNullException.ThrowIfNull(simulation); this.simulation = simulation;
		Baseline = simulation.CaptureSnapshot(); Commands = commands.AsReadOnly();
	}
	public void PrepareForTick()
	{
		if (simulation.Tick - Baseline.Tick < TickWindow && commands.Count < CommandWindow) return;
		// Capture BEFORE the next submission: already queued commands belong to the baseline,
		// while the next accepted command belongs only to the new recording.
		Baseline = simulation.CaptureSnapshot(); commands.Clear(); WindowsRolled++;
	}
	public CommandResult Submit(GameCommand command)
	{
		PrepareForTick(); var result = simulation.Submit(command);
		if (result == CommandResult.Accepted) commands.Add(new(simulation.Tick, command));
		return result;
	}
}
