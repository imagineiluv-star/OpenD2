using OpenD2.Core;

namespace OpenD2.Npc;

public enum NpcIntent { Greeting, QuestStatus, OfferInteraction }
public enum NpcStart { Accepted, Busy, InvalidInput, Unavailable }
public enum NpcOutcome { Answer, Timeout, ModelFailure, InvalidResponse }

// Owned immutable values only: adapters never receive the live simulation or engine objects.
public sealed record NpcFacts(EntityId Player, EntityId Npc, RegionId Region,
	string ContentHash, QuestProgress Quest, bool CanTalk);
public sealed record NpcRequest(long Id, long Generation, NpcFacts Facts, string Input);
public sealed record NpcReply(NpcIntent Intent, string Speech, bool OffersInteraction);
public sealed record NpcResult(NpcRequest Request, NpcOutcome Outcome, NpcReply Reply);

public interface INpcModel
{
	Task<string> RespondAsync(NpcRequest request, CancellationToken cancellationToken);
}

// Offline contract demo, not an LLM. Unknown language/input falls back to quest facts.
public sealed class ScriptedNpcModel : INpcModel
{
	public Task<string> RespondAsync(NpcRequest request, CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		string text = request.Input.Trim().ToLowerInvariant();
		var intent = text switch
		{
			"안녕" or "안녕하세요" or "hi" or "hello" => NpcIntent.Greeting,
			"수락" or "완료" or "accept" or "turn in" => NpcIntent.OfferInteraction,
			_ => NpcIntent.QuestStatus
		};
		return Task.FromResult($"{{\"intent\":\"{intent}\",\"targetId\":{request.Facts.Npc.Value}}}");
	}
}
