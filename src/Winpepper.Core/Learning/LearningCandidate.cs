namespace Winpepper.Core.Learning;

/// <summary>One accepted misheard-replacement candidate from a post-paste diff.</summary>
public sealed record LearningCandidate(string Wrong, string Right);
