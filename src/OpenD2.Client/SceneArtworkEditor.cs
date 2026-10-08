using Godot;
using OpenD2.Assets;

namespace OpenD2.Client;

public partial class SceneArtworkEditor : VBoxContainer
{
	private sealed record MotionDraft(string Path, string Layers, string Directions, double Fps);
	private sealed record ActorDraft(bool Enabled, string Palette, MotionDraft[] Motions, int Facing = 0);
	private sealed class MotionForm
	{
		public readonly LineEdit Path = new() { PlaceholderText = "MPQ DCC or COF path", MaxLength = 1023 };
		public readonly TextEdit Layers = new() { PlaceholderText = "COF only: component number=DCC path, one per line", CustomMinimumSize = new Vector2(400, 80) };
		public readonly LineEdit Directions = new() { PlaceholderText = "Eight verified indices, comma separated", MaxLength = 128 };
		public readonly SpinBox Fps = new() { MinValue = 0, MaxValue = 120, Step = 0, Value = 12 };
		public readonly Button Preview = new() { Text = "Inspect this motion in DCC-COF" };
		public MotionDraft Capture() => new(Path.Text, Layers.Text, Directions.Text, Fps.Value);
		public void Show(MotionDraft value) { Path.Text = value.Path; Layers.Text = value.Layers; Directions.Text = value.Directions; Fps.Value = value.Fps; }
		public LegacyMotionRequest Parse(string motion) => LegacyArtworkSetup.ParseMotion(motion, Path.Text, Layers.Text, Directions.Text, Fps.Value);
		public void SetBusy(bool value) { Path.Editable = !value; Layers.Editable = !value; Directions.Editable = !value; Fps.Editable = !value; Preview.Disabled = value; }
	}
	private readonly Func<string> gameDirectory;
	private readonly string sceneDirectory;
	private readonly Action<string> openScene;
	private readonly Func<string, LegacyMotionRequest, int, bool> inspectMotion;
	private readonly Button choose = new() { Text = "Open scene JSON for artwork" };
	private readonly Button save = new() { Text = "Validate and save a new scene copy", Disabled = true };
	private readonly Button loadSaved = new() { Text = "Load saved copy", Disabled = true };
	private readonly Label status = new() { AutowrapMode = TextServer.AutowrapMode.WordSmart };
	private readonly Label sourceInfo = new() { AutowrapMode = TextServer.AutowrapMode.WordSmart };
	private readonly VBoxContainer form = new() { Visible = false };
	private readonly OptionButton actor = new();
	private readonly CheckButton enabled = new() { Text = "Use artwork for this actor" };
	private readonly LineEdit palette = new() { PlaceholderText = "MPQ palette path", MaxLength = 1023 };
	private readonly OptionButton facing = new();
	private readonly OptionButton npcFacing = new() { Visible = false };
	private readonly TabContainer tabs = new();
	private uint SelectedId => selectedActor == source!.Actors.Length ? source.Npc.Id : source.Actors[selectedActor].Id;
	private readonly MotionForm[] motions = Enum.GetValues<ActorMotion>().Select(_ => new MotionForm()).ToArray();
	private readonly ConfirmationDialog replace = new() { Title = "Replace artwork form?", DialogText = "Unsaved form edits will be discarded if the new scene loads. Saved files and current gameplay are kept.", Exclusive = true };
	private Dictionary<uint, ActorDraft> drafts = new();
	private LegacySceneRequest? source;
	private string sourceDirectory = "", savedFile = "", pendingFile = "";
	private int selectedActor = -1;
	private bool busy;

	public SceneArtworkEditor(Func<string> gameDirectory, string sceneDirectory, Action<string> openScene, Func<string, LegacyMotionRequest, int, bool> inspectMotion)
	{ this.gameDirectory = gameDirectory; this.sceneDirectory = sceneDirectory; this.openScene = openScene; this.inspectMotion = inspectMotion; }
	public override void _Ready()
	{
		AddChild(new Label { Text = "Scene actor artwork" }); AddChild(choose); AddChild(sourceInfo);
		AddChild(new Label { Text = "Open a scene or use Map → Edit generated artwork. Configure combat actors' five motions or the guide's Idle, then save a new copy.\nThe original scene and running game are kept. Unconfigured artwork uses placeholders. Resource paths, direction indices and FPS must be verified in your owned data.", AutowrapMode = TextServer.AutowrapMode.WordSmart });
		AddChild(form); form.AddChild(actor); form.AddChild(enabled); form.AddChild(new Label { Text = "Actor palette" }); form.AddChild(palette);
		form.AddChild(new Label { Text = "Direction order: (-1,-1), (0,-1), (1,-1), (1,0), (1,1), (0,1), (-1,1), (-1,0)\nThese are world movement vectors. Values are source animation indices; inspect each facing.", AutowrapMode = TextServer.AutowrapMode.WordSmart });
		foreach (string vector in new[] { "(-1,-1)", "(0,-1)", "(1,-1)", "(1,0)", "(1,1)", "(0,1)", "(-1,1)", "(-1,0)" }) { facing.AddItem("Inspect facing " + vector); npcFacing.AddItem("Guide fixed facing " + vector); }
		form.AddChild(facing); form.AddChild(npcFacing); form.AddChild(tabs);
		for (int i = 0; i < motions.Length; i++)
		{
			int index = i; var fields = motions[i]; var panel = new VBoxContainer { Name = ((ActorMotion)i).ToString() }; tabs.AddChild(panel);
			panel.AddChild(fields.Path); panel.AddChild(fields.Layers); panel.AddChild(fields.Directions);
			panel.AddChild(new Label { Text = "Playback FPS" }); panel.AddChild(fields.Fps); panel.AddChild(fields.Preview);
			fields.Preview.Pressed += () => Inspect(index);
		}
		AddChild(save); AddChild(loadSaved); AddChild(status); AddChild(replace);
		var dialog = new FileDialog { FileMode = FileDialog.FileModeEnum.OpenFile, Access = FileDialog.AccessEnum.Filesystem, Filters = ["*.json ; Legacy scene request"] }; AddChild(dialog);
		choose.Pressed += () => { if (!busy) dialog.PopupCenteredRatio(0.7f); }; dialog.FileSelected += RequestOpen;
		replace.Confirmed += () => { string file = pendingFile; pendingFile = ""; _ = ReadScene(file); };
		replace.Canceled += () => pendingFile = "";
		actor.ItemSelected += index => ShowActor((int)index);
		save.Pressed += () => _ = SaveCopy(); loadSaved.Pressed += () => { if (!busy && savedFile.Length > 0) openScene(savedFile); };
		if (OS.GetCmdlineUserArgs().Contains("--smoke-test")) Callable.From(() => { _ = Smoke(); }).CallDeferred();
	}
	public void RequestOpen(string file)
	{
		if (busy || pendingFile.Length > 0) return;
		if (source is null) _ = ReadScene(file);
		else { pendingFile = file; replace.PopupCentered(); }
	}
	private void SetBusy(bool value)
	{
		busy = value; choose.Disabled = value; actor.Disabled = value; enabled.Disabled = value; palette.Editable = !value; facing.Disabled = value; npcFacing.Disabled = value;
		foreach (var motion in motions) motion.SetBusy(value);
		save.Disabled = value || source is null; loadSaved.Disabled = value || savedFile.Length == 0;
	}
	private void CaptureActor()
	{
		if (source is not null && selectedActor >= 0) drafts[SelectedId] = new(enabled.ButtonPressed, palette.Text, motions.Select(m => m.Capture()).ToArray(), npcFacing.Selected);
	}
	private void ShowActor(int index)
	{
		CaptureActor(); selectedActor = index; actor.Select(index);
		bool isNpc = index == source!.Actors.Length; npcFacing.Visible = isNpc; tabs.CurrentTab = 0;
		for (int i = 1; i < motions.Length; i++) tabs.SetTabHidden(i, isNpc);
		var draft = drafts[SelectedId]; npcFacing.Select(draft.Facing); enabled.SetPressedNoSignal(draft.Enabled); palette.Text = draft.Palette;
		for (int i = 0; i < motions.Length; i++) motions[i].Show(draft.Motions[i]);
	}
	private async Task<bool> ReadScene(string file, Func<string, byte[]>? read = null)
	{
		if (busy) return false;
		SetBusy(true); status.Text = "Checking scene and source resources...";
		try
		{
			string directory = gameDirectory();
			var request = await Task.Run(() =>
			{
				var next = LegacySceneRequest.Read(file);
				_ = read is null ? LegacyPlayScene.Load(directory, next) : LegacyPlayScene.Load(next, read);
				return next;
			});
			if (!IsInstanceValid(this) || !IsInsideTree()) return false;
			var nextDrafts = request.Actors.ToDictionary(a => a.Id, a =>
			{
				var art = request.Artwork?.SingleOrDefault(v => v.Entity == a.Id);
				return new ActorDraft(art is not null, art?.PalettePath ?? request.Regions.Single(r => r.Id == a.Region).Terrain.PalettePath,
					Enum.GetNames<ActorMotion>().Select(name =>
					{
						var m = art?.Motions.Single(v => v.Motion == name);
						return new MotionDraft(m?.Path ?? "", m?.Layers is null ? "" : string.Join('\n', m.Layers.OrderBy(p => p.Key).Select(p => $"{p.Key}={p.Value}")), m is null ? "" : string.Join(',', m.Directions), m?.Fps ?? 12);
					}).ToArray());
			});
			var npcArt = request.NpcArtwork;
			nextDrafts.Add(request.Npc.Id, new(npcArt is not null, npcArt?.PalettePath ?? request.Regions.Single(r => r.Id == request.Npc.Region).Terrain.PalettePath,
				Enum.GetNames<ActorMotion>().Select(name =>
				{
					var m = name == "Idle" ? npcArt?.Idle : null;
					return new MotionDraft(m?.Path ?? "", m?.Layers is null ? "" : string.Join('\n', m.Layers.OrderBy(p => p.Key).Select(p => $"{p.Key}={p.Value}")), m is null ? "" : string.Join(',', m.Directions), m?.Fps ?? 12);
				}).ToArray(), npcArt?.Facing ?? 0));
			source = request; sourceDirectory = directory; drafts = nextDrafts; selectedActor = -1; savedFile = ""; actor.Clear();
			foreach (var spawn in request.Actors) actor.AddItem($"{(spawn.Player ? "Player" : "Monster")} {spawn.Id} · region {spawn.Region}");
			actor.AddItem($"Guide {request.Npc.Id} · {request.Npc.Name} · Idle only");
			ShowActor(0); form.Show(); sourceInfo.Text = "Source: " + Path.GetFullPath(file);
			status.Text = "Scene opened. Existing artwork is preserved. Palette defaults to the actor's region; verify it for each actor. Saving always creates a new file.";
			return true;
		}
		catch (Exception error) { if (IsInstanceValid(this) && IsInsideTree()) status.Text = "Open failed; current form and files retained. " + error.Message; return false; }
		finally { if (IsInstanceValid(this) && IsInsideTree()) SetBusy(false); }
	}
	private void Inspect(int motion)
	{
		if (busy || source is null) return;
		try
		{
			if (gameDirectory() != sourceDirectory) throw new InvalidDataException("Data directory changed. Reopen the scene before inspecting or saving.");
			if (AssetDecoders.Kind(palette.Text) != "palette") throw new InvalidDataException("Choose an actor palette path.");
			if (!inspectMotion(palette.Text, motions[motion].Parse(((ActorMotion)motion).ToString()), facing.Selected)) status.Text = "Animation inspector is busy. Try again when it finishes.";
		}
		catch (Exception error) { status.Text = "Inspect failed: " + error.Message; }
	}
	private async Task<bool> SaveCopy(Func<string, byte[]>? read = null, string? file = null)
	{
		if (busy || source is null) return false;
		SetBusy(true); status.Text = "Checking all actor motions, terrain and quest connectivity...";
		try
		{
			if (gameDirectory() != sourceDirectory) throw new InvalidDataException("Data directory changed. Reopen the scene before saving.");
			CaptureActor();
			var artwork = drafts.Where(p => p.Value.Enabled && p.Key != source.Npc.Id).OrderBy(p => p.Key).Select(p => new LegacyActorRequest(p.Key, p.Value.Palette,
				p.Value.Motions.Select((m, i) => LegacyArtworkSetup.ParseMotion(((ActorMotion)i).ToString(), m.Path, m.Layers, m.Directions, m.Fps)).ToArray())).ToArray();
			var npcDraft = drafts[source.Npc.Id]; var idle = npcDraft.Motions[0];
			LegacyNpcRequest? npcArt = npcDraft.Enabled ? new(source.Npc.Id, npcDraft.Palette,
				LegacyArtworkSetup.ParseMotion("Idle", idle.Path, idle.Layers, idle.Directions, idle.Fps), npcDraft.Facing) : null;
			var request = source with { Artwork = artwork, NpcArtwork = npcArt }; string directory = sourceDirectory;
			file ??= Path.Combine(sceneDirectory, "artwork-" + Guid.NewGuid().ToString("N") + ".json");
			await Task.Yield();
			var ready = await Task.Run(() => LegacySceneSetup.SaveNew(file, request, read ?? (path => AssetDecoders.ReadFromInstall(directory, path))));
			if (!IsInstanceValid(this) || !IsInsideTree()) return false;
			savedFile = file;
			status.Text = $"Saved copy: {file}\nCombat actor artwork: {ready.ActorsWithArtwork}/{ready.Actors}; guide artwork: {ready.NpcArtworkConfigured}; static quest loop: {ready.QuestLoopReachable}. GUI QA: NOT_RUN.\nLoad saved copy uses this snapshot. Later edits require saving another copy. Artwork changes use a separate checkpoint slot. Guide walking/talking motions, original rules and visual accuracy remain unverified.";
			return true;
		}
		catch (Exception error) { if (IsInstanceValid(this) && IsInsideTree()) status.Text = "Save failed; original scene, previous copies and current game retained. " + error.Message; return false; }
		finally { if (IsInstanceValid(this) && IsInsideTree()) SetBusy(false); }
	}
}
