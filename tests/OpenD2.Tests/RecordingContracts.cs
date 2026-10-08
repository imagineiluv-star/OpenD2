using OpenD2.Core;

internal static class RecordingContracts
{
	private static void Check(bool value) { if (!value) throw new Exception("Recording assertion failed."); }
	private static void ReplayMatches(GameSimulation simulation, SimulationRecording recording) =>
		Check(GameSimulation.Replay(recording.Baseline, recording.Commands, simulation.Tick).ComputeStateHash() == simulation.ComputeStateHash());
	public static void Run(Action<string, Action> test)
	{
		test("recording rolls for two simulated hours without limiting live tick progress", () =>
		{
			var simulation = new GameSimulation(1, [new(new(1), new(1), new(0, 0))]); var recording = new SimulationRecording(simulation);
			for (int i = 0; i < 180001; i++) { recording.PrepareForTick(); simulation.Step(); }
			Check(simulation.Tick == 180001 && recording.WindowsRolled == 12 && recording.Commands.Count == 0 && recording.Baseline.Tick == 180000);
			ReplayMatches(simulation, recording);
		});
		test("recording command rollover captures queued commands exactly once", () =>
		{
			var simulation = new GameSimulation(1, [new(new(1), new(1), new(0, 0))]); var recording = new SimulationRecording(simulation);
			for (ulong n = 1; n < SimulationRecording.CommandWindow; n++)
			{ Check(recording.Submit(new(simulation.Tick + 1, n, new(1), new(1), CommandKind.Signal)) == CommandResult.Accepted); simulation.Step(); }
			Check(recording.Submit(new(simulation.Tick + 1, 4096, new(1), new(1), CommandKind.Signal)) == CommandResult.Accepted);
			Check(recording.Submit(new(simulation.Tick + 1, 4097, new(1), new(1), CommandKind.SetMove, 1)) == CommandResult.Accepted);
			Check(recording.WindowsRolled == 1 && recording.Baseline.PendingCommands.Count == 1 && recording.Commands.Count == 1);
			ReplayMatches(simulation, recording); simulation.Step(); ReplayMatches(simulation, recording);
		});
		test("rejected commands do not enter recording and restored sessions use their own baseline", () =>
		{
			var simulation = new GameSimulation(1, [new(new(1), new(1), new(0, 0))]); var recording = new SimulationRecording(simulation);
			Check(recording.Submit(new(0, 1, new(1), new(1), CommandKind.Signal)) == CommandResult.ExpiredTick && recording.Commands.Count == 0);
			Check(recording.Submit(new(2, 1, new(1), new(1), CommandKind.Signal)) == CommandResult.Accepted); simulation.Step();
			var restored = GameSimulation.Restore(simulation.CaptureSnapshot()); var resumed = new SimulationRecording(restored);
			Check(resumed.Baseline.Tick == 1 && resumed.Baseline.PendingCommands.Count == 1);
			restored.Step(); ReplayMatches(restored, resumed);
		});
	}
}
