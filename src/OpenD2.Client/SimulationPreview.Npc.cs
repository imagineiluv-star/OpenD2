using Godot;
using OpenD2.Npc;
using System.Diagnostics;

namespace OpenD2.Client;

public partial class SimulationPreview
{
	private readonly LocalNpcRuntime npcRuntime = new();
	private readonly NpcModelStore npcStore;
	private readonly OptionButton npcModels = new();
	private readonly Button npcDownload = new() { Text = "Download model" };
	private readonly Button npcStart = new() { Text = "Start local AI" };
	private readonly Button npcStop = new() { Text = "Use basic dialogue" };
	private readonly Button npcRemove = new() { Text = "Remove model" };
	private readonly Button npcCancel = new() { Text = "Cancel" };
	private readonly Label npcStatus = new() { AutowrapMode = TextServer.AutowrapMode.WordSmart };
	private readonly Label npcMode = new() { AutowrapMode = TextServer.AutowrapMode.WordSmart };
	private Task<string>? npcOperation;
	private CancellationTokenSource? npcCancellation;
	private long npcDownloadBytes;
	private string npcOperationName = "", npcModelName = "";
	private bool npcRuntimeSmoke;
	private long npcSettingsNextRefresh;
	private string npcExecutable = "";
	private NpcModelSpec SelectedNpcModel => NpcModelCatalog.All[npcModels.Selected];
	private sealed class ByteProgress(Action<long> report) : IProgress<long> { public void Report(long value) => report(value); }
	private void BuildNpcSettings()
	{
		// Godot's editor loads managed assemblies from bytes, so Assembly.Location is empty there.
		npcExecutable = LocalNpcRuntime.ExecutableIn(OS.HasFeature("editor") ? ProjectSettings.GlobalizePath("res://.godot/mono/temp/bin/Debug") : AppContext.BaseDirectory);
		playPanel.AddChild(new Label { Text = "로컬 AI는 선택 기능입니다. 아래 용량을 인터넷으로 내려받으며 Apache-2.0 모델을 사용합니다.\n의도 오분류가 가능한 실험입니다. 대사와 보상은 게임 규칙이 결정합니다. 모델을 실행하면 CPU와 RAM을 사용합니다.", AutowrapMode = TextServer.AutowrapMode.WordSmart });
		foreach (var model in NpcModelCatalog.All) npcModels.AddItem($"{model.Name} — {model.Bytes / 1000000.0:F0} MB");
		npcModels.Selected = 0; playPanel.AddChild(npcModels);
		var actions = new HFlowContainer(); playPanel.AddChild(actions);
		foreach (var button in new[] { npcDownload, npcStart, npcStop, npcRemove, npcCancel }) actions.AddChild(button);
		playPanel.AddChild(npcMode); playPanel.AddChild(npcStatus);
		npcDownload.Pressed += () => BeginNpcOperation("download");
		npcStart.Pressed += () => BeginNpcOperation("start");
		npcStop.Pressed += () => BeginNpcOperation("stop");
		npcRemove.Pressed += () => BeginNpcOperation("remove");
		npcCancel.Pressed += () => { _ = npcCancellation?.CancelAsync(); };
		npcStatus.Text = "모델 없이 기본 대사로 바로 플레이할 수 있습니다. 대화는 이 PC에서 처리하며 저장하지 않습니다.";
		if (smokeTest)
		{
			npcRuntimeSmoke = true; npcCancellation = new(); npcOperationName = "smoke";
			npcOperation = Task.Run(async () => { await LocalNpcRuntime.VerifyBundledAsync(npcCancellation.Token, npcExecutable); return "Bundled runtime verified; no model downloaded."; });
		}
		PollNpcSettings();
	}
	private void BeginNpcOperation(string name)
	{
		if (npcOperation is not null) return;
		ResetDialogue(); var selected = SelectedNpcModel; npcOperationName = name; npcCancellation = new();
		var token = npcCancellation.Token; Interlocked.Exchange(ref npcDownloadBytes, 0);
		npcStatus.Text = name switch { "download" => "다운로드 준비 중…", "start" => "모델 무결성 확인 및 실행 중…", "remove" => "모델 제거 중…", _ => "기본 대사로 전환 중…" };
		npcOperation = Task.Run(async () =>
		{
			await npcRuntime.StopAsync();
			switch (name)
			{
				case "download":
					await npcStore.InstallAsync(selected, new ByteProgress(value => Interlocked.Exchange(ref npcDownloadBytes, value)), token);
					return "다운로드와 무결성 검사가 끝났습니다. Start local AI로 실행하세요.";
				case "start":
					string path = await npcStore.VerifyAsync(selected, token);
					await npcRuntime.StartAsync(npcExecutable, path, token);
					return "로컬 AI가 준비됐습니다. 한국어로 임무를 물어보세요.";
				case "remove": token.ThrowIfCancellationRequested(); npcStore.Remove(selected); return "모델을 제거했습니다. 기본 대사로 계속 플레이할 수 있습니다.";
				default: return "기본 대사를 사용합니다. 다운로드한 모델은 보관됩니다.";
			}
		});
		npcModelName = selected.Name;
	}
	private void PollNpcSettings()
	{
		long now = Stopwatch.GetTimestamp(); if (now < npcSettingsNextRefresh) return;
		npcSettingsNextRefresh = now + Stopwatch.Frequency / 4;
		if (npcOperation is { IsCompleted: true } finished)
		{
			try
			{
				npcStatus.Text = finished.GetAwaiter().GetResult();
				if (npcRuntimeSmoke) GD.Print("OPEND2_NPC02_RUNTIME_READY");
			}
			catch (OperationCanceledException) { npcStatus.Text = "작업을 취소했거나 응답이 지연됐습니다. 다운로드는 다시 누르면 이어받습니다."; }
			catch (Exception error)
			{
				npcStatus.Text = $"로컬 AI 작업 실패 ({error.GetType().Name}). 기본 대사로 플레이하거나 다운로드/실행을 다시 시도하세요.";
				log("npc_runtime_failure", error.GetType().Name);
				if (npcRuntimeSmoke) GD.PushError("Bundled NPC runtime smoke failed: " + error.GetType().Name);
			}
			bool startSmokeDialogue = npcRuntimeSmoke && npcSmokePending;
			npcRuntimeSmoke = false; npcOperation = null; npcCancellation?.Dispose(); npcCancellation = null;
			if (startSmokeDialogue) SendDialogue();
		}
		bool busy = npcOperation is not null, supported = LocalNpcRuntime.IsHardwareSupported && File.Exists(npcExecutable);
		if (busy && npcOperationName == "download") npcStatus.Text = $"다운로드 {Interlocked.Read(ref npcDownloadBytes) / 1000000.0:F1} / {SelectedNpcModel.Bytes / 1000000.0:F1} MB — 취소 후 이어받기 가능";
		npcMode.Text = !supported ? "이 PC의 선택형 AI 런타임은 지원되지 않습니다. 기본 대사는 사용할 수 있습니다." :
			npcRuntime.IsRunning ? $"로컬 AI 사용 중: {npcModelName}" : npcRuntime.Enabled ? "로컬 AI가 중단됐습니다. 응답 실패 시 기본 대사를 표시합니다. 다시 실행할 수 있습니다." : "기본 대사 사용 중 — 모델 자동 실행 없음";
		npcModels.Disabled = busy || npcRuntime.Enabled;
		npcDownload.Disabled = busy || !supported;
		npcStart.Disabled = busy || !supported || !npcStore.IsInstalled(SelectedNpcModel);
		npcStop.Disabled = busy || !npcRuntime.Enabled;
		npcRemove.Disabled = busy || !npcStore.HasDownload(SelectedNpcModel);
		npcCancel.Disabled = !busy || npcOperationName is "stop" or "remove" or "smoke";
	}
}
