using OpenD2.Core;
using System.Text.Json;

namespace OpenD2.Npc;

public static class NpcDecisionGate
{
	public const int MaxInputChars = 512, MaxResponseChars = 256;

	// Call on the simulation owner thread. Region/range/line of sight match Core interactions.
	public static NpcFacts Capture(GameSimulation simulation)
	{
		if (simulation.World is not { } world) throw new ArgumentException("NPC dialogue requires a world.", nameof(simulation));
		var player = simulation.GetEntity(simulation.WorldPlayer); var npc = world.QuestGiver;
		long dx = (long)player.Position.X - npc.Position.X, dy = (long)player.Position.Y - npc.Position.Y;
		bool available = player.IsAlive && player.HitStun == 0 && player.Region == npc.Region &&
			dx * dx + dy * dy <= (long)GameSimulation.AttackRange * GameSimulation.AttackRange && world.GetRegion(player.Region).Collision.HasMeleeLine(player.Position, npc.Position);
		return new(player.Id, npc.Id, player.Region, world.ContentHash, simulation.Quest, available);
	}
	public static bool IsCurrent(NpcRequest request, long generation, NpcFacts current) =>
		request.Generation == generation && current.CanTalk && request.Facts == current;

	public static bool TryDecode(string? json, NpcFacts facts, out NpcReply? reply)
	{
		reply = null;
		if (!facts.CanTalk || json is null || json.Length > MaxResponseChars) return false;
		try
		{
			using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 4 });
			if (doc.RootElement.ValueKind != JsonValueKind.Object) return false;
			string? intentName = null; uint? target = null; int count = 0;
			foreach (var property in doc.RootElement.EnumerateObject())
			{
				count++;
				if (property.Name == "intent" && intentName is null && property.Value.ValueKind == JsonValueKind.String) intentName = property.Value.GetString();
				else if (property.Name == "targetId" && target is null && property.Value.ValueKind == JsonValueKind.Number && property.Value.TryGetUInt32(out uint id)) target = id;
				else return false; // Reject duplicates, unknown fields and executable/freeform payloads.
			}
			if (count != 2 || target != facts.Npc.Value || intentName is not ("Greeting" or "QuestStatus" or "OfferInteraction")) return false;
			var intent = Enum.Parse<NpcIntent>(intentName);
			reply = Render(intent, facts); return true;
		}
		catch (JsonException) { return false; }
	}

	// No freeform model claims are displayed in NPC-01. Facts and text come from the host.
	public static NpcReply Render(NpcIntent intent, NpcFacts facts)
	{
		string speech = intent == NpcIntent.Greeting ? "안녕하세요. 지하실 임무를 안내해 드릴게요." : facts.Quest.Stage switch
		{
			QuestStage.Available => "지하실의 적을 처치해 주세요. 임무 수락은 확인 버튼으로 진행하세요.",
			QuestStage.Active => $"현재 {facts.Quest.Required}명 중 {facts.Quest.Defeated}명을 처치했습니다. 남은 적을 처치하고 돌아오세요.",
			QuestStage.ReadyToTurnIn => "목표를 모두 처치했습니다. 확인 버튼으로 임무를 완료할 수 있습니다.",
			QuestStage.Completed => "이미 완료한 임무입니다. 체력 회복 보상은 한 번만 지급됩니다.",
			_ => throw new ArgumentOutOfRangeException(nameof(facts))
		};
		bool offer = facts.CanTalk && intent == NpcIntent.OfferInteraction && facts.Quest.Stage is QuestStage.Available or QuestStage.ReadyToTurnIn;
		return new(intent, speech, offer);
	}
	public static bool CanConfirm(NpcResult result, long generation, NpcFacts current) =>
		result.Outcome == NpcOutcome.Answer && result.Reply.OffersInteraction &&
		result.Reply.Intent == NpcIntent.OfferInteraction && IsCurrent(result.Request, generation, current) &&
		current.Quest.Stage is QuestStage.Available or QuestStage.ReadyToTurnIn;
}
