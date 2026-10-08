using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace OpenD2.Npc;

// Immutable catalog + exact size/hash: no remotely supplied install paths or executable archives.
public sealed class NpcModelStore : IDisposable
{
	private readonly string root;
	private readonly HttpClient client;
	public NpcModelStore(string root) : this(root, new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(120) }) { }
	internal NpcModelStore(string root, HttpClient client) { this.root = Path.GetFullPath(root); this.client = client; }
	public string PathFor(NpcModelSpec model) { model.Validate(); return Path.Combine(root, model.Id + ".gguf"); }
	public bool IsInstalled(NpcModelSpec model) => File.Exists(PathFor(model)); // UI hint only; Start verifies bytes.
	public bool HasDownload(NpcModelSpec model) => IsInstalled(model) || File.Exists(PathFor(model) + ".partial");
	private static void RejectLink(string path)
	{
		if (new FileInfo(path).LinkTarget is not null || Directory.Exists(path)) throw new IOException("Model file must be a regular file.");
	}
	private FileStream Lock(NpcModelSpec model)
	{
		if (new DirectoryInfo(root).LinkTarget is not null) throw new IOException("Model directory cannot be a symbolic link.");
		Directory.CreateDirectory(root);
		string path = PathFor(model);
		foreach (string suffix in new[] { "", ".partial", ".lock" }) RejectLink(path + suffix);
		return new(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
	}
	private static async Task<bool> Matches(string path, NpcModelSpec model, CancellationToken token)
	{
		RejectLink(path);
		if (!File.Exists(path)) return false;
		await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
		if (file.Length != model.Bytes) return false;
		return Convert.ToHexStringLower(await SHA256.HashDataAsync(file, token).ConfigureAwait(false)) == model.Sha256;
	}
	public async Task<string> VerifyAsync(NpcModelSpec model, CancellationToken token)
	{
		using var guard = Lock(model); string path = PathFor(model);
		if (!await Matches(path, model, token).ConfigureAwait(false)) throw new InvalidDataException("NPC model is missing or its size/hash does not match. Download it again.");
		return path;
	}
	private async Task<HttpResponseMessage> Download(Uri uri, long offset, CancellationToken token)
	{
		for (int hop = 0; hop < 6; hop++)
		{
			if (uri.Scheme != "https" || uri.UserInfo.Length != 0) throw new InvalidDataException("Model download must remain HTTPS.");
			using var request = new HttpRequestMessage(HttpMethod.Get, uri);
			if (offset > 0) request.Headers.Range = new RangeHeaderValue(offset, null);
			var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
			if (response.StatusCode is not (HttpStatusCode.Moved or HttpStatusCode.Redirect or HttpStatusCode.RedirectMethod or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)) return response;
			using (response)
			{
				var location = response.Headers.Location ?? throw new InvalidDataException("Missing model redirect location.");
				uri = location.IsAbsoluteUri ? location : new Uri(uri, location);
			}
		}
		throw new InvalidDataException("Too many model redirects.");
	}
	public async Task InstallAsync(NpcModelSpec model, IProgress<long>? progress, CancellationToken token)
	{
		using var guard = Lock(model); string path = PathFor(model), partial = path + ".partial";
		if (await Matches(path, model, token).ConfigureAwait(false)) { progress?.Report(model.Bytes); return; }
		try
		{
			await using (var file = new FileStream(partial, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan))
			{
				if (file.Length > model.Bytes) file.SetLength(0);
				long offset = file.Length;
				if (offset < model.Bytes)
				{
					using var response = await Download(model.DownloadUri, offset, token).ConfigureAwait(false);
					if (response.StatusCode == HttpStatusCode.OK) { offset = 0; file.SetLength(0); }
					else if (response.StatusCode == HttpStatusCode.PartialContent)
					{
						var range = response.Content.Headers.ContentRange;
						if (range?.Unit != "bytes" || range.From != offset || range.To != model.Bytes - 1 || range.Length != model.Bytes)
							throw new InvalidDataException("Invalid model resume range.");
					}
					else { response.EnsureSuccessStatusCode(); throw new InvalidDataException("Unexpected model download status."); }
					if (response.Content.Headers.ContentLength is { } length && length != model.Bytes - offset) throw new InvalidDataException("Invalid model download length.");
					file.Position = offset; progress?.Report(offset);
					await using var body = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
					using var readDeadline = CancellationTokenSource.CreateLinkedTokenSource(token); byte[] buffer = new byte[65536];
					while (true)
					{
						readDeadline.CancelAfter(TimeSpan.FromSeconds(30));
						int read = await body.ReadAsync(buffer, readDeadline.Token).ConfigureAwait(false);
						if (read == 0) break;
						if (read > model.Bytes - offset) throw new InvalidDataException("Model download exceeded its size budget.");
						await file.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false); offset += read; progress?.Report(offset);
					}
					if (offset != model.Bytes) throw new IOException("Model download interrupted. Retry to resume.");
				}
				await file.FlushAsync(token).ConfigureAwait(false); file.Flush(flushToDisk: true);
			}
			if (!await Matches(partial, model, token).ConfigureAwait(false)) throw new InvalidDataException("NPC model checksum mismatch.");
			token.ThrowIfCancellationRequested(); File.Move(partial, path, overwrite: true); progress?.Report(model.Bytes);
		}
		catch (InvalidDataException) { if (File.Exists(partial)) File.Delete(partial); throw; }
	}
	// Caller stops its owned runtime first. Other processes holding a model cause normal I/O failure.
	public void Remove(NpcModelSpec model)
	{
		using var guard = Lock(model); string path = PathFor(model);
		File.Delete(path); File.Delete(path + ".partial");
	}
	public void Dispose() => client.Dispose();
}
