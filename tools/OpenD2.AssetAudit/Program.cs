using System.Text.Json;
using OpenD2.Assets;

if (args.Length != 1 || args[0] is "--help" or "-h")
{
	Console.WriteLine("Usage: OpenD2.AssetAudit <game-data-directory>\nM0 directory check only; MPQ inventory/decoding is not implemented.");
	return args.Length == 1 ? 0 : 2;
}
try
{
	var path = DataDirectory.Validate(args[0]);
	Console.WriteLine(JsonSerializer.Serialize(new { directory = path, readable = true, contentVerified = false, stage = "M0" }));
	return 0;
}
catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
{
	Console.Error.WriteLine(error.Message);
	return 1;
}
