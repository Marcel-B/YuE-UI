namespace YueUI.Api.Lyrics;

/// <summary>Lyrics to change rather than write anew, and what to change in them.</summary>
/// <param name="Lyrics">The text as it stands, typically the last draft, edited or not.</param>
/// <param name="Instruction">A short request such as "make the chorus catchier" or "a sadder second verse".</param>
public sealed record LyricsRevision(string Lyrics, string Instruction);
