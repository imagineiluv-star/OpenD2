namespace OpenD2.Assets;

public static class AssetDecoders
{
	public const string Version = "m1.07-1";
	public const int MaxInputBytes = 33554432;
	public static string? Kind(string logicalPath)
	{
		string path = MpqArchive.NormalizePath(logicalPath);
		if (path.StartsWith("data\\global\\excel\\") && path.EndsWith(".txt")) return "excel_txt";
		if (path.EndsWith(".dt1")) return "dt1";
		if (path.EndsWith(".ds1")) return "ds1";
		if (path.EndsWith(".dcc")) return "dcc";
		if (path.EndsWith(".cof")) return "cof";
		if (path.EndsWith(".dc6")) return "dc6";
		if (path.StartsWith("data\\global\\palette\\") && path.EndsWith("\\pal.dat")) return "palette";
		if (path.StartsWith("data\\local\\lng\\") && path.EndsWith(".tbl")) return "text_tbl";
		return null;
	}
	public static void Validate(string kind, byte[] data)
	{
		switch (kind)
		{
			case "excel_txt": ExcelTextTable.Parse(data); break;
			case "ds1": Ds1Map.Parse(data); break;
			case "dt1":
				var tiles = Dt1Tileset.Parse(data);
				for (int t = 0; t < tiles.Tiles.Count; t++) tiles.DecodeTile(t);
				break;
			case "cof": CofAnimation.Parse(data); break;
			case "dcc":
				var animation = DccAnimation.Parse(data);
				for (int d = 0; d < animation.Directions; d++) animation.DecodeDirection(d);
				break;
			case "palette": Palette.Parse(data); break;
			case "text_tbl": StringTable.Parse(data); break;
			case "dc6":
				var image = Dc6Image.Parse(data);
				for (int d = 0; d < image.Directions; d++)
					for (int f = 0; f < image.FramesPerDirection; f++) image.DecodeFrame(d, f);
				break;
			default: throw new ArgumentException("Unsupported decoder.", nameof(kind));
		}
	}
	public static byte[] ReadFromInstall(string directory, string logicalPath)
	{
		logicalPath = MpqArchive.NormalizePath(logicalPath);
		foreach (string archivePath in GameInstall.Probe(directory).Archives)
		{
			// Failure to open a higher-priority archive is not silently bypassed.
			using var archive = new MpqArchive(archivePath);
			try { return archive.Read(logicalPath, MaxInputBytes); }
			catch (MpqException error) when (error.Code == 2) { }
		}
		throw new FileNotFoundException("Resource not found in installation MPQs.", logicalPath);
	}
}
