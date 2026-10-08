using OpenD2.Core;
using OpenD2.Assets;
using System.Text.Json;

var root = Path.Combine(Path.GetTempPath(), "opend2-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var failures = 0;
var count = 0;
void Test(string name, Action action)
{
	count++;
	try { action(); Console.WriteLine($"PASS {name}"); }
	catch (Exception e) { failures++; Console.Error.WriteLine($"FAIL {name}: {e}"); }
}
void Check(bool value) { if (!value) throw new Exception("Assertion failed"); }
void Throws<T>(Action action) where T : Exception
{
	try { action(); } catch (T) { return; }
	throw new Exception($"Expected {typeof(T).Name}");
}
try
{
	LegacyFormatContracts.Run(Test);
	AnimationContracts.Run(Test);
	MapContracts.Run(Test);
	TableContracts.Run(root, Test);
	PlayAssetContracts.Run(root, Test);
	PlaySceneContracts.Run(root, Test);
	ActorArtContracts.Run(Test);
	NavigationContracts.Run(Test);
	PlayReadinessContracts.Run(Test);
	RecordingContracts.Run(Test);
	MpqContracts.Run(root, Test);
	SimulationContracts.Run(Test);
	CombatContracts.Run(Test);
	WorldContracts.Run(Test);
	ItemContracts.Run(Test);
	SaveContracts.Run(root, Test);
	LegacySaveContracts.Run(root, Test);
	NpcContracts.Run(Test);
Npc02Contracts.Run(Test);
	Test("paths create separate directories", () =>
	{
		var paths = new AppPaths(Path.Combine(root, "user")); paths.EnsureCreated();
		Check(Directory.Exists(paths.Saves) && Directory.Exists(paths.Cache) && Directory.Exists(paths.Logs));
	});
	Test("missing settings use defaults", () => Check(AppSettings.Load(Path.Combine(root, "absent")) == new AppSettings()));
	Test("settings round trip and preserve previous backup", () =>
	{
		var path = Path.Combine(root, "settings.json");
		var first = new AppSettings(GameDataPath: "한글 데이터", MaxFps: 120, Fullscreen: true, ShowDiagnostics: true); first.Save(path);
		new AppSettings(MaxFps: 30).Save(path);
		Check(AppSettings.Load(path).MaxFps == 30 && AppSettings.Load(path + ".bak") == first);
		Check(!Directory.EnumerateFiles(root, "*.tmp").Any());
	});
	Test("invalid settings cannot replace valid file", () =>
	{
		var path = Path.Combine(root, "valid.json"); new AppSettings().Save(path);
		Throws<InvalidDataException>(() => new AppSettings(MaxFps: 0).Save(path));
		Check(AppSettings.Load(path).MaxFps == 60);
	});
	Test("existing settings gain safe display defaults without changing source bytes", () =>
	{
		var path = Path.Combine(root, "existing-settings.json"); const string data = "{\"SchemaVersion\":1,\"GameDataPath\":\"\",\"MaxFps\":120}";
		File.WriteAllText(path, data); var settings = AppSettings.Load(path);
		Check(settings.MaxFps == 120 && !settings.Fullscreen && !settings.ShowDiagnostics && File.ReadAllText(path) == data);
	});
	Test("future schema rejected without overwrite", () =>
	{
		var path = Path.Combine(root, "future.json"); const string data = "{\"SchemaVersion\":99}";
		File.WriteAllText(path, data); Throws<InvalidDataException>(() => AppSettings.Load(path)); Check(File.ReadAllText(path) == data);
	});
	Test("malformed and oversized settings rejected", () =>
	{
		var path = Path.Combine(root, "broken.json"); File.WriteAllText(path, "{");
		Throws<JsonException>(() => AppSettings.Load(path)); File.WriteAllText(path, new string(' ', 65537));
		Throws<InvalidDataException>(() => AppSettings.Load(path));
	});
	Test("directory check does not claim content compatibility", () =>
	{
		Check(DataDirectory.Validate(root) == Path.GetFullPath(root));
		Throws<DirectoryNotFoundException>(() => DataDirectory.Validate(Path.Combine(root, "missing")));
		Throws<ArgumentException>(() => DataDirectory.Validate(""));
	});
	Test("metrics bounded window and percentile", () =>
	{
		var metrics = new FrameMetrics(); Check(metrics.P95Milliseconds() == 0);
		for (int i = 1; i <= 100; i++) metrics.Record(i / 1000.0);
		Check(Math.Abs(metrics.P95Milliseconds() - 95) < 0.001);
		for (int i = 0; i < 600; i++) metrics.Record(0.002);
		Check(Math.Abs(metrics.P95Milliseconds() - 2) < 0.001);
		Throws<ArgumentOutOfRangeException>(() => metrics.Record(double.NaN));
	});
	Test("log writes parseable JSON and retains ten sessions", () =>
	{
		var dir = Path.Combine(root, "logs"); Directory.CreateDirectory(dir);
		for (int i = 0; i < 12; i++) File.WriteAllText(Path.Combine(dir, $"session-old-{i}.jsonl"), "{}");
		using (var log = new SessionLog(dir)) log.Write("startup", "test\nmessage");
		Check(Directory.GetFiles(dir).Length == 10);
		foreach (var file in Directory.GetFiles(dir)) using (JsonDocument.Parse(File.ReadAllText(file))) { }
	});
}
finally { Directory.Delete(root, recursive: true); }
Console.WriteLine($"{count - failures}/{count} passed");
return failures == 0 ? 0 : 1;
