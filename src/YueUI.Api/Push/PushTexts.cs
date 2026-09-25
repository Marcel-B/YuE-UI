using YueUI.Api.Worker;

namespace YueUI.Api.Push;

/// <summary>What a notification says. Made here rather than in the frontend's i18n.ts, since no page may be open.</summary>
public static class PushTexts
{
    public static (string Title, string Body) Song(SongState song, string language)
    {
        var de = IsGerman(language);
        var name = string.IsNullOrWhiteSpace(song.Title) ? song.Run : song.Title;
        var which = $"{name} · Song {song.Index}";
        return song.Stage == "ready"
            ? (de ? "Song fertig" : "Song finished",
                song.Seconds is { } seconds ? $"{which} · {Duration(seconds)}" : which)
            : (de ? "Song fehlgeschlagen" : "Song failed", WithMessage(which, song.Message));
    }

    public static (string Title, string Body) Transcription(TranscriptionState transcription, string language)
    {
        var de = IsGerman(language);
        return transcription.Stage == "done"
            ? (de ? "Transkription fertig" : "Transcription finished", transcription.FileName)
            : (de ? "Transkription fehlgeschlagen" : "Transcription failed", WithMessage(transcription.FileName, transcription.Message));
    }

    public static (string Title, string Body) Lyrics(LyricsState lyrics, string language)
    {
        var de = IsGerman(language);
        return lyrics.Stage == "done"
            ? (de ? "Songtext fertig" : "Lyrics ready", de ? "Der Entwurf steht im Formular." : "The draft is in the form.")
            : (de ? "Songtext fehlgeschlagen" : "Lyrics failed", lyrics.Message ?? "");
    }

    public static (string Title, string Body) Test(string language) => IsGerman(language)
        ? ("YuE UI", "Benachrichtigungen funktionieren.")
        : ("YuE UI", "Notifications work.");

    private static bool IsGerman(string language) => language.StartsWith("de", StringComparison.OrdinalIgnoreCase);

    private static string WithMessage(string subject, string? message) =>
        string.IsNullOrWhiteSpace(message) ? subject : $"{subject}: {message}";

    private static string Duration(double seconds)
    {
        var total = (int)Math.Round(seconds);
        return $"{total / 60}:{total % 60:00}";
    }
}
