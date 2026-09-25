namespace YueUI.Api.Lyrics;

/// <param name="Id">The model's key in LM Studio, what a draft request names.</param>
/// <param name="Name">LM Studio's display name; the id where there is none.</param>
/// <param name="SizeBytes">Size on disk, roughly the memory it takes; null when the server does not say.</param>
/// <param name="Loaded">Already in memory in LM Studio, so a draft uses it as it is.</param>
public sealed record LyricsModel(string Id, string Name, long? SizeBytes, bool Loaded);

/// <param name="Default">The configured model (<see cref="LyricsOptions.Model"/>), used when a request names none.</param>
public sealed record LyricsModels(string Default, IReadOnlyList<LyricsModel> Models);
