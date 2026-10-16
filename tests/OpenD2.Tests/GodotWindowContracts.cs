using System.Diagnostics;
using OpenD2.Assets;

// Real Godot window vs the CPU --window model, compared with ScreenDiff. Needs a display (DISPLAY or WAYLAND_DISPLAY),
// a Godot binary (GODOT or .local-tools/godot-path.txt), the client built for Godot, and D2_DATA_PATH; otherwise SKIP.
// Window captures go to a temp folder outside the repository and are deleted afterwards.
internal static class GodotWindowContracts
{
	static void Check(bool value, string what) { if (!value) throw new Exception("Assertion failed " + what); }

	public static void Run(Action<string, Action> test)
	{
		var dirInfo = new DirectoryInfo(AppContext.BaseDirectory);
		while (dirInfo is not null && !File.Exists(Path.Combine(dirInfo.FullName, "OpenD2.sln"))) dirInfo = dirInfo.Parent;
		if (dirInfo is null) { Console.WriteLine("SKIP Godot window tests: repository root not found"); return; }
		string root = dirInfo.FullName;
		string? data = Environment.GetEnvironmentVariable(D2DataPaths.DataPathVariable);
		string client = Path.Combine(root, "src", "OpenD2.Client");
		string? godot = Environment.GetEnvironmentVariable("GODOT");
		string pathFile = Path.Combine(root, ".local-tools", "godot-path.txt");
		if (string.IsNullOrWhiteSpace(godot) && File.Exists(pathFile)) godot = File.ReadAllText(pathFile).Trim();
		string? missing =
			string.IsNullOrWhiteSpace(data) ? "D2_DATA_PATH is not set"
			: string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DISPLAY")) && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY")) ? "no display"
			: string.IsNullOrWhiteSpace(godot) || !File.Exists(godot) ? "no Godot binary (set GODOT or run eng/bootstrap.py)"
			: !File.Exists(Path.Combine(client, ".godot", "mono", "temp", "bin", "Debug", "OpenD2.Client.dll")) ? "client not built for Godot (godot --headless --path src/OpenD2.Client --build-solutions --quit)"
			: null;
		if (missing is not null) { Console.WriteLine($"SKIP Godot window tests: {missing}"); return; }

		// (scale, filter, window, exact): whole-number scales and the below-1x path (CPU box filter shown 1:1) must match
		// the CPU model exactly. Fractional scales go through GPU sampling: bilinear rounding for sharp, and float ties at
		// source-pixel edges for nearest (measured 99.98% exact at 1.667x on Mesa), so they get an error tolerance.
		(string Scale, string Filter, int W, int H, bool Exact)[] cases =
		[
			("fit", "nearest", 1700, 1000, false),
			("integer", "sharp", 1700, 1000, true),
			("fit", "sharp", 700, 500, true),
			("fit", "sharp", 1700, 1000, false),
		];
		foreach (var c in cases)
			test($"godot window: {c.Scale}/{c.Filter} at {c.W}x{c.H} matches the CPU --window render", () =>
			{
				string dir = Path.Combine(Path.GetTempPath(), "opend2-window-" + Guid.NewGuid().ToString("N"));
				Directory.CreateDirectory(dir);
				try
				{
					string shot = Path.Combine(dir, "window.png");
					var start = new ProcessStartInfo(godot!) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
					foreach (var arg in new[] { "--path", client, "--rendering-driver", "opengl3", "--position", "0,0", "--resolution", $"{c.W}x{c.H}", "--",
						"--original-mode", "--scale", c.Scale, "--filter", c.Filter, "--capture-window", shot, "--capture-frames", "15" }) start.ArgumentList.Add(arg);
					start.Environment[D2DataPaths.DataPathVariable] = data;
					using var process = Process.Start(start)!;
					var output = process.StandardOutput.ReadToEndAsync(); var errors = process.StandardError.ReadToEndAsync();
					if (!process.WaitForExit(90_000)) { process.Kill(true); throw new Exception("Godot timed out"); }
					Check(process.ExitCode == 0 && File.Exists(shot), $"godot exit {process.ExitCode}: {output.Result.Split('\n').LastOrDefault(l => l.Contains("OPEND2"))} {errors.Result[^Math.Min(300, errors.Result.Length)..]}");
					var gpu = PngReader.DecodeRgb(File.ReadAllBytes(shot));
					using var mpq = new MpqAssetSource(data!);
					byte[] Read(string p) => mpq.TryRead(p, AssetDecoders.MaxInputBytes, out var b) ? b : throw new FileNotFoundException(p);
					// The window manager may shrink the window; model the size Godot actually rendered.
					var cpu = LegacyScreenRenderer.Render(new LegacyCaptureRequest("gameselect", LegacyResolution.R800x600, "units", WindowWidth: gpu.Width, WindowHeight: gpu.Height,
						ScaleMode: LegacyScaleOptions.ParseMode(c.Scale), Filter: LegacyScaleOptions.ParseFilter(c.Filter)), Read);
					var diff = ScreenDiff.Compare(gpu.Width, gpu.Height, gpu.Rgb, cpu.Rgb);
					Console.WriteLine($"  {gpu.Width}x{gpu.Height} rect {cpu.Layout}: match {diff.MatchPercent:0.###}% MAE {diff.MeanAbsoluteError:0.###} SSIM {diff.Ssim:0.#####}");
					Check(c.Exact ? diff.MatchPercent == 100 : diff.MeanAbsoluteError <= 0.5 && diff.Ssim >= 0.99, $"match {diff.MatchPercent} MAE {diff.MeanAbsoluteError} SSIM {diff.Ssim}");
				}
				finally { Directory.Delete(dir, true); }
			});
	}
}
