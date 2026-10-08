using OpenD2.Core;
using OpenD2.Npc;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

internal static class Npc02Contracts
{
	private static readonly NpcFacts Facts = new(new(1), new(10), new(1), "test", new(QuestStage.Available, 0, 3), true);
	private static readonly NpcRequest Request = new(1, 0, Facts, "지하실 임무를 맡겠습니다.");
	private const string Decision = "{\"intent\":\"OfferInteraction\",\"targetId\":10}";
	private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
	{ protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => send(request, token); }
	private static HttpClient Client(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) => new(new Handler(send)) { BaseAddress = new("http://127.0.0.1:19876/") };
	private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
	private static HttpResponseMessage Body(byte[] value, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = new ByteArrayContent(value) };
	private static void Check(bool value) { if (!value) throw new Exception("NPC-02 assertion failed."); }
	private static async Task Throws<T>(Func<Task> task) where T : Exception
	{ try { await task(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }
	private static NpcModelSpec Spec(byte[] bytes) => NpcModelCatalog.Small with { Bytes = bytes.Length, Sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)) };
	private static async Task WithStore(Func<string, Task> run)
	{
		string root = Path.Combine(Path.GetTempPath(), "opend2-npc02-" + Guid.NewGuid()); Directory.CreateDirectory(root);
		try { await run(root); } finally { Directory.Delete(root, true); }
	}
	private static HttpClient ModelClient(Func<string, JsonElement, HttpResponseMessage> respond) => Client(async (request, token) =>
	{
		using var payload = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
		return respond(request.RequestUri!.AbsolutePath, payload.RootElement);
	});
	private static HttpResponseMessage Completion(string content = Decision, string stop = "eos", bool truncated = false, int predicted = 16) =>
		Json(new { content, stop_type = stop, truncated, tokens_predicted = predicted });
	private static HttpResponseMessage Default(string route) => route switch
	{ "/apply-template" => Json(new { prompt = "templated" }), "/tokenize" => Json(new { tokens = new[] { 11, 22, 33 } }), _ => Completion() };
	public static void Run(Action<string, Action> test)
	{
		void Async(string name, Func<Task> body) => test(name, () => body().GetAwaiter().GetResult());
		Async("NPC local adapter sends exact budgeted tokens and constrained target without model speech", async () =>
		{
			var routes = new List<string>();
			using var model = new LlamaNpcModel(ModelClient((route, payload) =>
			{
				routes.Add(route);
				if (route == "/apply-template") { Check(payload.GetProperty("messages")[1].GetProperty("content").GetString() == Request.Input); Check(!payload.GetProperty("chat_template_kwargs").GetProperty("enable_thinking").GetBoolean()); }
				if (route == "/tokenize") Check(payload.GetProperty("content").GetString() == "templated");
				if (route == "/completion")
				{
					Check(payload.GetProperty("prompt").EnumerateArray().Select(x => x.GetInt32()).SequenceEqual(new[] { 11, 22, 33 }));
					Check(payload.GetProperty("n_predict").GetInt32() == 96 && !payload.GetProperty("stream").GetBoolean() && !payload.GetProperty("cache_prompt").GetBoolean());
					var schema = payload.GetProperty("json_schema"); Check(!schema.GetProperty("additionalProperties").GetBoolean());
					Check(schema.GetProperty("properties").GetProperty("targetId").GetProperty("enum")[0].GetInt32() == 10);
				}
				return Default(route);
			}));
			Check(await model.RespondAsync(Request, default) == Decision); Check(routes.SequenceEqual(new[] { "/apply-template", "/tokenize", "/completion" }));
		});
		Async("NPC token budget prevents inference before completion and rejects malformed token ids", async () =>
		{
			foreach (int[] tokens in new[] { Array.Empty<int>(), new int[2049], new[] { -1 } })
			{
				using var model = new LlamaNpcModel(ModelClient((route, _) => route switch
				{ "/apply-template" => Default(route), "/tokenize" => Json(new { tokens }), _ => throw new Exception("Inference should not run") }));
				await Throws<InvalidDataException>(() => model.RespondAsync(Request, default));
			}
		});
		Async("NPC adapter rejects token limit, context truncation, wrong target and unsupported commands", async () =>
		{
			foreach (var invalid in new[] { Completion(stop: "limit"), Completion(truncated: true), Completion(predicted: 97), Completion(predicted: 0),
				Completion(Decision.Replace("10", "11")), Completion("{\"intent\":\"GiveGold\",\"targetId\":10}"), Completion("{\"intent\":\"Greeting\",\"targetId\":10,\"speech\":\"reward\"}") })
			{
				using var model = new LlamaNpcModel(ModelClient((route, _) => route == "/completion" ? invalid : Default(route)));
				await Throws<InvalidDataException>(() => model.RespondAsync(Request, default));
			}
		});
		Async("NPC adapter bounds HTTP body and template before tokenization", async () =>
		{
			foreach (var response in new[] { Json(new { prompt = new string('x', 8193) }), Json(new { prompt = new string('x', 33000) }), new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") } })
			{
				using var model = new LlamaNpcModel(ModelClient((route, _) => route == "/apply-template" ? response : throw new Exception("Unexpected request")));
				await Throws<InvalidDataException>(() => model.RespondAsync(Request, default));
			}
		});
		Async("NPC inference cancellation reaches transport and rejects redirect responses", async () =>
		{
			var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
			using var model = new LlamaNpcModel(Client(async (_, token) => { entered.SetResult(); await Task.Delay(Timeout.Infinite, token); return Json(new { }); }));
			using var cancel = new CancellationTokenSource(); var task = model.RespondAsync(Request, cancel.Token); await entered.Task; cancel.Cancel();
			await Throws<OperationCanceledException>(() => task);
			using var redirect = new LlamaNpcModel(Client((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Redirect))));
			await Throws<HttpRequestException>(() => redirect.RespondAsync(Request, default));
		});
		Async("NPC public endpoint refuses external hosts credentials paths and missing session keys", async () =>
		{
			foreach (string uri in new[] { "https://127.0.0.1:5000/", "http://example.com:5000/", "http://localhost:5000/", "http://127.0.0.1:80/", "http://127.0.0.1:5000/api", "http://user@127.0.0.1:5000/", "http://127.0.0.1:5000/?q=x" })
				await Throws<ArgumentException>(() => { using var model = new LlamaNpcModel(new Uri(uri), new string('a', 64)); return Task.CompletedTask; });
			await Throws<ArgumentException>(() => { using var model = new LlamaNpcModel(new("http://127.0.0.1:5000/"), ""); return Task.CompletedTask; });
		});
		Async("NPC unhealthy or malformed runtime stays unavailable", async () =>
		{
			using var loading = new LlamaNpcModel(Client((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)))); Check(!await loading.ReadyAsync(default));
			using var ready = new LlamaNpcModel(Client((_, _) => Task.FromResult(Json(new { status = "ok" })))); Check(await ready.ReadyAsync(default));
			using var broken = new LlamaNpcModel(Client((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)))); await Throws<HttpRequestException>(() => broken.ReadyAsync(default));
		});
		Async("NPC verified download is atomic and repeated install performs no network request", () => WithStore(async root =>
		{
			byte[] bytes = Encoding.UTF8.GetBytes("synthetic-gguf-fixture"); var spec = Spec(bytes); int calls = 0;
			using var store = new NpcModelStore(root, Client((_, _) => { calls++; return Task.FromResult(Body(bytes)); }));
			await store.InstallAsync(spec, null, default); Check(await store.VerifyAsync(spec, default) == store.PathFor(spec));
			await store.InstallAsync(spec, null, default); Check(calls == 1 && !File.Exists(store.PathFor(spec) + ".partial"));
			store.Remove(spec); Check(!store.IsInstalled(spec));
		}));
		Async("NPC model resume validates Content-Range and restarts when server ignores Range", () => WithStore(async root =>
		{
			byte[] bytes = Enumerable.Range(0, 100).Select(n => (byte)n).ToArray(); var spec = Spec(bytes);
			foreach (bool resumes in new[] { true, false })
			{
				using var store = new NpcModelStore(root, Client((request, _) =>
				{
					Check(request.Headers.Range!.Ranges.Single().From == 25);
					var response = Body(resumes ? bytes[25..] : bytes, resumes ? HttpStatusCode.PartialContent : HttpStatusCode.OK);
					if (resumes) response.Content.Headers.ContentRange = new(25, 99, 100); return Task.FromResult(response);
				}));
				File.WriteAllBytes(store.PathFor(spec) + ".partial", bytes[..25]);
				await store.InstallAsync(spec, null, default); Check(File.ReadAllBytes(await store.VerifyAsync(spec, default)).SequenceEqual(bytes)); store.Remove(spec);
			}
		}));
		Async("NPC bad checksum never replaces an installed file and discards corrupt partial", () => WithStore(async root =>
		{
			byte[] bytes = [1, 2, 3, 4]; var spec = Spec(bytes);
			using var store = new NpcModelStore(root, Client((_, _) => Task.FromResult(Body(new byte[] { 4, 3, 2, 1 }))));
			File.WriteAllText(store.PathFor(spec), "previous-file");
			await Throws<InvalidDataException>(() => store.InstallAsync(spec, null, default));
			Check(File.ReadAllText(store.PathFor(spec)) == "previous-file" && !File.Exists(store.PathFor(spec) + ".partial"));
		}));
		Async("NPC truncated body keeps a resumable partial and wrong ranges are rejected", () => WithStore(async root =>
		{
			byte[] bytes = [1, 2, 3, 4]; var spec = Spec(bytes);
			using var first = new NpcModelStore(root, Client((_, _) => { var response = Body(bytes[..2]); response.Content.Headers.ContentLength = 4; return Task.FromResult(response); }));
			await Throws<IOException>(() => first.InstallAsync(spec, null, default)); Check(new FileInfo(first.PathFor(spec) + ".partial").Length == 2);
			using var second = new NpcModelStore(root, Client((_, _) => { var response = Body(bytes[2..], HttpStatusCode.PartialContent); response.Content.Headers.ContentRange = new(1, 2, 4); return Task.FromResult(response); }));
			await Throws<InvalidDataException>(() => second.InstallAsync(spec, null, default)); Check(!File.Exists(second.PathFor(spec) + ".partial"));
		}));
		Async("NPC HTTPS redirects preserve resume range and reject downgrade before sending", () => WithStore(async root =>
		{
			byte[] bytes = [1, 2, 3, 4]; var spec = Spec(bytes);
			foreach (bool secure in new[] { true, false })
			{
				int calls = 0;
				using var store = new NpcModelStore(root, Client((request, _) =>
				{
					calls++; Check(request.Headers.Range!.Ranges.Single().From == 2);
					if (calls == 1) { var response = new HttpResponseMessage(HttpStatusCode.Redirect); response.Headers.Location = new((secure ? "https" : "http") + "://cdn.example/model"); return Task.FromResult(response); }
					Check(secure && request.RequestUri!.Host == "cdn.example"); var body = Body(bytes[2..], HttpStatusCode.PartialContent); body.Content.Headers.ContentRange = new(2, 3, 4); return Task.FromResult(body);
				}));
				File.WriteAllBytes(store.PathFor(spec) + ".partial", bytes[..2]);
				if (secure) { await store.InstallAsync(spec, null, default); Check(calls == 2); store.Remove(spec); }
				else { await Throws<InvalidDataException>(() => store.InstallAsync(spec, null, default)); Check(calls == 1); }
			}
		}));
		Async("NPC redirect loops and oversized bodies stop within fixed budgets", () => WithStore(async root =>
		{
			var spec = Spec(new byte[] { 1 }); int calls = 0;
			using var loop = new NpcModelStore(root, Client((_, _) => { calls++; var response = new HttpResponseMessage(HttpStatusCode.Redirect); response.Headers.Location = new("/again", UriKind.Relative); return Task.FromResult(response); }));
			await Throws<InvalidDataException>(() => loop.InstallAsync(spec, null, default)); Check(calls == 6);
			using var big = new NpcModelStore(root, Client((_, _) => { var response = Body(new byte[] { 1, 2 }); response.Content.Headers.ContentLength = 1; return Task.FromResult(response); }));
			await Throws<InvalidDataException>(() => big.InstallAsync(spec, null, default)); Check(!big.IsInstalled(spec));
		}));
		Async("NPC install cancellation retains partial and releases exclusive install lock", () => WithStore(async root =>
		{
			var spec = Spec(new byte[] { 1, 2, 3 }); using var cancel = new CancellationTokenSource();
			var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
			using var store = new NpcModelStore(root, Client(async (_, token) => { entered.SetResult(); await Task.Delay(Timeout.Infinite, token); return Body([]); }));
			File.WriteAllBytes(store.PathFor(spec) + ".partial", [1]);
			var task = store.InstallAsync(spec, null, cancel.Token); await entered.Task;
			await Throws<IOException>(() => { store.Remove(spec); return Task.CompletedTask; });
			cancel.Cancel(); await Throws<OperationCanceledException>(() => task); Check(new FileInfo(store.PathFor(spec) + ".partial").Length == 1);
			store.Remove(spec); Check(!File.Exists(store.PathFor(spec) + ".partial"));
		}));
		Async("NPC catalog rejects traversal unpinned sources and invalid fingerprints", () => WithStore(async root =>
		{
			using var store = new NpcModelStore(root, Client((_, _) => throw new Exception("No network expected")));
			foreach (var spec in new[] { NpcModelCatalog.Small with { Id = "../escape" }, NpcModelCatalog.Small with { Repository = "untrusted/model" }, NpcModelCatalog.Small with { Revision = "main" }, NpcModelCatalog.Small with { Sha256 = "bad" }, NpcModelCatalog.Small with { FileName = "../test.gguf" }, NpcModelCatalog.Small with { Bytes = 0 } })
				await Throws<ArgumentException>(() => store.InstallAsync(spec, null, default));
		}));
		Async("NPC runtime default and stopped mode work without a model or network", async () =>
		{
			using var runtime = new LocalNpcRuntime(); Check(!runtime.Enabled && !runtime.IsRunning);
			Check(NpcDecisionGate.TryDecode(await runtime.RespondAsync(Request with { Input = "안녕" }, default), Facts, out var reply) && reply!.Intent == NpcIntent.Greeting);
			await runtime.StopAsync(); await runtime.StopAsync(); Check(!runtime.Enabled);
		});
		Async("NPC bundled native runtime executes and invalid model startup cleans up for retry", () => WithStore(async root =>
		{
			await LocalNpcRuntime.VerifyBundledAsync(default);
			string bad = Path.Combine(root, "invalid.gguf"); File.WriteAllText(bad, "invalid model");
			using var runtime = new LocalNpcRuntime();
			for (int i = 0; i < 2; i++)
			{
				await Throws<IOException>(() => runtime.StartAsync(LocalNpcRuntime.BundledExecutable, bad, default));
				Check(!runtime.IsRunning && !runtime.Enabled); await runtime.StopAsync();
			}
			using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
			await Throws<OperationCanceledException>(() => runtime.StartAsync(LocalNpcRuntime.BundledExecutable, bad, cancelled.Token)); Check(!runtime.IsRunning);
		}));
	}
}
