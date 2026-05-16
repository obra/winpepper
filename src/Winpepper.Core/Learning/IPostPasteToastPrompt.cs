namespace Winpepper.Core.Learning;

/// <summary>
/// Renders the non-modal "Learn correction: wrong → right? [Yes / Preferred / No]"
/// toast and resolves with the user's choice. Spec §8.2 (5). Implementations
/// enforce the 8 s timeout themselves and return <see cref="PostPasteDecision.No"/>
/// when it elapses.
/// </summary>
public interface IPostPasteToastPrompt
{
    Task<PostPasteDecision> AskAsync(LearningCandidate candidate, CancellationToken ct);
}
