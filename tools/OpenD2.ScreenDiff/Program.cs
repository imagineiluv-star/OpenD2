using System.Text.Json;
using OpenD2.Assets;

// Headless original-mode capture and screen comparison. Captures are written outside the repository only.
const string Usage = """
Usage: OpenD2.ScreenDiff --capture <scene> --out <file.png> [--data <game-data-dir>] [--resolution 800x600|640x480] [--palette units] [--seed N] [--tick N] [--window WxH] [--scale fit|integer] [--filter sharp|nearest|linear]
       OpenD2.ScreenDiff --diff <a.png> <b.png> [--min-match <percent>] [--max-mae <value>] [--min-ssim <value>]
--capture renders the internal 800x600/640x480 buffer on the CPU unless --window asks for the presented window (default: fit scale, sharp filter) (no GPU, no window); the same commit, scene and options give byte-identical PNGs.
Scenes: gameselect, charselect. --data defaults to D2_DATA_PATH. The output must lie outside any Git checkout.
--diff prints pixel match %, mean absolute error (0-255) and SSIM as JSON. Exit 0: within thresholds; 3: outside; 1: error; 2: usage.
""";
if (args.Length == 0 || args[0] is "--help" or "-h") { Console.WriteLine(Usage); return args.Length == 0 ? 2 : 0; }
string? Option(string name) { int i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
var json = new JsonSerializerOptions { WriteIndented = true };
try
{
	if (args[0] == "--capture" && args.Length >= 2)
	{
		string output = Path.GetFullPath(Option("--out") ?? throw new ArgumentException("--out is required."));
		D2DataPaths.RequireOutsideGit(Path.GetDirectoryName(output)!);
		string data = D2DataPaths.ResolveDataPath(Option("--data")) ?? throw new ArgumentException("Pass --data or set D2_DATA_PATH.");
		int? ww = null, wh = null;
		if (Option("--window") is { } window)
		{
			var parts = window.ToLowerInvariant().Split('x');
			if (parts.Length != 2 || !int.TryParse(parts[0], out int w) || !int.TryParse(parts[1], out int h) || w is < 1 or > 16384 || h is < 1 or > 16384) throw new ArgumentException("--window must be WIDTHxHEIGHT.");
			(ww, wh) = (w, h);
		}
		var request = new LegacyCaptureRequest(args[1], LegacyResolutions.Parse(Option("--resolution") ?? "800x600"), Option("--palette") ?? "units",
			int.Parse(Option("--seed") ?? "0"), int.Parse(Option("--tick") ?? "0"), ww, wh, LegacyScaleOptions.ParseMode(Option("--scale") ?? "fit"), LegacyScaleOptions.ParseFilter(Option("--filter") ?? "sharp"));
		using var source = new MpqAssetSource(data);
		byte[] Read(string path) => source.TryRead(path, AssetDecoders.MaxInputBytes, out var bytes) ? bytes : throw new FileNotFoundException("Missing " + path);
		var capture = LegacyScreenRenderer.Render(request, Read);
		byte[] png = LegacyScreenRenderer.EncodePng(capture);
		Directory.CreateDirectory(Path.GetDirectoryName(output)!);
		File.WriteAllBytes(output, png);
		Console.WriteLine(JsonSerializer.Serialize(new { request, output, capture.Width, capture.Height, capture.Layout, capture.Sources,
			sha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(png)).ToLowerInvariant() }, json));
		return 0;
	}
	if (args[0] == "--diff" && args.Length >= 3)
	{
		var a = PngReader.DecodeRgb(File.ReadAllBytes(args[1])); var b = PngReader.DecodeRgb(File.ReadAllBytes(args[2]));
		if (a.Width != b.Width || a.Height != b.Height) { Console.WriteLine(JsonSerializer.Serialize(new { error = "size mismatch", a = new[] { a.Width, a.Height }, b = new[] { b.Width, b.Height } }, json)); return 3; }
		var result = ScreenDiff.Compare(a.Width, a.Height, a.Rgb, b.Rgb);
		double minMatch = double.Parse(Option("--min-match") ?? "100", System.Globalization.CultureInfo.InvariantCulture);
		double maxMae = double.Parse(Option("--max-mae") ?? "0", System.Globalization.CultureInfo.InvariantCulture);
		double minSsim = double.Parse(Option("--min-ssim") ?? "0", System.Globalization.CultureInfo.InvariantCulture);
		bool pass = result.MatchPercent >= minMatch && result.MeanAbsoluteError <= maxMae && result.Ssim >= minSsim;
		Console.WriteLine(JsonSerializer.Serialize(new { result, pass }, json));
		return pass ? 0 : 3;
	}
	Console.Error.WriteLine(Usage); return 2;
}
catch (Exception error) when (error is IOException or InvalidDataException or ArgumentException or FormatException or UnauthorizedAccessException or DllNotFoundException or InvalidOperationException)
{
	Console.Error.WriteLine(error.Message); return 1;
}
