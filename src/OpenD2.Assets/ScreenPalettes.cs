namespace OpenD2.Assets;

/// <summary>
/// Which global palette (data\global\palette\NAME\pal.dat + pal.pl2) each original screen uses. The rules follow the
/// original client's PAL_* usage (Shared/D2Shared.hpp, Modcode/Client/UI/Menus/*.cpp and Panels/*.cpp):
/// Trademark and the character-select panel switch the global palette to sky; character creation to fechar; the
/// loading screen to loading; front-end stitched backgrounds (game select, TCP/IP) and popups are drawn with units;
/// the world uses act1..act5 for acts 1..5; endgame/endgame2 are the classic/expansion end screens.
/// </summary>
public static class ScreenPalettes
{
	/// <summary>Bump when a rule changes so asset caches that picked palettes automatically rebuild.</summary>
	public const string RulesVersion = "screen-palettes-1";
	public const string Auto = "auto";
	public static readonly IReadOnlyList<string> Names =
		["act1", "act2", "act3", "act4", "act5", "endgame", "endgame2", "fechar", "loading", "menu0", "menu1", "menu2", "menu3", "menu4", "sky", "static", "trademark", "units"];

	/// <summary>Screen (capture scene) to palette. Unknown screens are not guessed.</summary>
	public static readonly IReadOnlyDictionary<string, string> Screens = new SortedDictionary<string, string>(StringComparer.Ordinal)
	{
		["trademark"] = "sky",       // Trademark.cpp: SetGlobalPalette(PAL_SKY)
		["gameselect"] = "units",    // OtherMultiplayer.cpp: gameselectscreenEXP.dc6 with PAL_UNITS
		["tcpip"] = "units",         // TCPIP.cpp: TCPIPscreen.dc6 with PAL_UNITS
		["charselect"] = "fechar",   // CharCreate.cpp: SetGlobalPalette(PAL_FECHAR), charactercreationscreenEXP.dc6
		["charlist"] = "sky",        // Panels/CharSelect.cpp: SetGlobalPalette(PAL_SKY)
		["loading"] = "loading",     // Loading.cpp: SetGlobalPalette(PAL_LOADING)
		["endgame"] = "endgame",
		["endgame2"] = "endgame2",
	};

	public static string ForScene(string scene) => Screens.TryGetValue(scene, out var p) ? p
		: throw new ArgumentException($"No palette rule for screen '{scene}'. Known: {string.Join(", ", Screens.Keys)}.");

	/// <summary>World palette for an act (1-5).</summary>
	public static string ForAct(int act) => act is >= 1 and <= 5 ? "act" + act : throw new ArgumentOutOfRangeException(nameof(act), "Act must be 1-5.");

	/// <summary>
	/// Default palette for converting a resource (asset cache PNGs): act tiles/monsters under an act folder use that act;
	/// character-creation art fechar; loading art loading; trademark/logo art sky; everything else units, the palette the
	/// original shares between all global palettes for UI, items and units.
	/// </summary>
	public static string ForAssetPath(string logicalPath)
	{
		string p = MpqArchive.NormalizePath(logicalPath);
		var parts = p.Split('\\');
		foreach (var part in parts)
			if (part.Length == 4 && part.StartsWith("act", StringComparison.Ordinal) && part[3] is >= '1' and <= '5') return part;
		string file = parts[^1];
		if (p.StartsWith("data\\global\\ui\\frontend\\", StringComparison.Ordinal))
		{
			if (file.StartsWith("charactercreation", StringComparison.Ordinal) || file.StartsWith("charactercreate", StringComparison.Ordinal)) return "fechar";
			if (file.StartsWith("trademark", StringComparison.Ordinal)) return "sky";
		}
		if (p.StartsWith("data\\global\\ui\\loading\\", StringComparison.Ordinal)) return "loading";
		return "units";
	}

	public static string Validate(string name)
	{
		string n = name.Trim().ToLowerInvariant();
		if (!Names.Contains(n)) throw new ArgumentException($"Unknown palette '{name}'. Known: {string.Join(", ", Names)}.");
		return n;
	}

	/// <summary>"auto" (or null/empty) picks the screen's rule; anything else must be a palette folder name.</summary>
	public static string Resolve(string? requested, string scene) =>
		string.IsNullOrWhiteSpace(requested) || requested.Trim().Equals(Auto, StringComparison.OrdinalIgnoreCase) ? ForScene(scene) : Validate(requested);

	public static string DatPath(string name) => $"data\\global\\palette\\{name}\\pal.dat";
	public static string Pl2Path(string name) => $"data\\global\\palette\\{name}\\pal.pl2";
}

/// <summary>
/// Gamma ramp applied to final RGB, as the original applies its "gamma" video option through the display gamma ramp
/// after palette lookup. out = round(255 * (in/255)^(1/gamma)); 1.0 is the identity (the default, so captures without
/// --gamma are unchanged). The exact 1.14d slider-to-gamma steps are not documented; this is the standard power ramp
/// (estimated, to be checked against original captures).
/// </summary>
public static class LegacyGamma
{
	public const double Min = 0.5, Max = 3.0, Default = 1.0;
	public static byte[] Table(double gamma)
	{
		if (double.IsNaN(gamma) || gamma < Min || gamma > Max) throw new ArgumentOutOfRangeException(nameof(gamma), $"Gamma must be {Min}-{Max}.");
		var t = new byte[256];
		for (int i = 0; i < 256; i++) t[i] = gamma == 1.0 ? (byte)i : (byte)Math.Clamp((int)Math.Round(255 * Math.Pow(i / 255.0, 1 / gamma)), 0, 255);
		return t;
	}
	public static bool IsIdentity(double gamma) => gamma == 1.0;
}

/// <summary>
/// How a sprite is composited in the 8-bit back buffer, using the act's PL2 tables like the original: the source index
/// is first remapped through the light table (0 = darkest, 31 = full brightness), then combined with the destination
/// index through a 256x256 blend table (25/50/75% transparency, additive, multiplicative). Opaque = plain copy.
/// </summary>
public enum LegacyBlendMode { Opaque, Trans25, Trans50, Trans75, Additive, Multiplicative }

public readonly record struct LegacyDrawEffect(LegacyBlendMode Blend = LegacyBlendMode.Opaque, int? Light = null)
{
	public static readonly LegacyDrawEffect None = new();
	public bool NeedsPl2 => Blend != LegacyBlendMode.Opaque || Light is not null;
	public static Pl2Blend ToPl2(LegacyBlendMode mode) => mode switch
	{
		LegacyBlendMode.Trans25 => Pl2Blend.Alpha25, LegacyBlendMode.Trans50 => Pl2Blend.Alpha50, LegacyBlendMode.Trans75 => Pl2Blend.Alpha75,
		LegacyBlendMode.Additive => Pl2Blend.Additive, LegacyBlendMode.Multiplicative => Pl2Blend.Multiplicative,
		_ => throw new ArgumentOutOfRangeException(nameof(mode)),
	};
	public static LegacyBlendMode Parse(string text) => text.Trim().ToLowerInvariant() switch
	{
		"opaque" or "none" => LegacyBlendMode.Opaque, "25" or "trans25" => LegacyBlendMode.Trans25, "50" or "trans50" => LegacyBlendMode.Trans50,
		"75" or "trans75" => LegacyBlendMode.Trans75, "add" or "additive" => LegacyBlendMode.Additive, "mul" or "multiplicative" => LegacyBlendMode.Multiplicative,
		_ => throw new ArgumentException("Blend must be opaque, 25, 50, 75, additive or multiplicative."),
	};
}

/// <summary>Shared parsing of the --palette/--light/--gamma options so the client, --capture and ScreenDiff agree.</summary>
public static class LegacyRenderOptions
{
	public static int? ParseLight(string? text)
	{
		if (text is null) return null;
		if (!int.TryParse(text, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int level) || (uint)level >= Pl2Palette.LightLevels)
			throw new ArgumentException("--light must be 0-31.");
		return level;
	}
	public static double ParseGamma(string? text)
	{
		if (text is null) return LegacyGamma.Default;
		if (!double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double g) || double.IsNaN(g) || g < LegacyGamma.Min || g > LegacyGamma.Max)
			throw new ArgumentException($"--gamma must be {LegacyGamma.Min}-{LegacyGamma.Max}.");
		return g;
	}
}
