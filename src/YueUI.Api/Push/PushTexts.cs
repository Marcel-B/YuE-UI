using YueUI.Api.Voices;
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

    public static (string Title, string Body) Version(VersionState version, string language)
    {
        var de = IsGerman(language);
        var song = version.SongId[(version.SongId.IndexOf('/') + 1)..].Replace("song", "Song ", StringComparison.Ordinal);
        var which = $"{(string.IsNullOrWhiteSpace(version.Title) ? version.Run : version.Title)} · {song} · {version.VoiceLabel}";
        return version.Stage == "done"
            ? (de ? "Stimme fertig" : "Voice finished", which)
            : (de ? "Stimme fehlgeschlagen" : "Voice failed", WithMessage(which, version.Message));
    }

    public static (string Title, string Body) Stems(StemSetState set, string language)
    {
        var de = IsGerman(language);
        var which = $"{(string.IsNullOrWhiteSpace(set.Title) ? set.Run : set.Title)} · {set.Song.Replace("song", "Song ", StringComparison.Ordinal)}";
        return set.Stage == "done"
            ? (de ? "Stems fertig" : "Stems ready", which)
            : (de ? "Stems fehlgeschlagen" : "Stems failed", WithMessage(which, set.Message));
    }

    public static (string Title, string Body) Lyrics(LyricsState lyrics, string language)
    {
        var de = IsGerman(language);
        return lyrics.Stage == "done"
            ? (de ? "Songtext fertig" : "Lyrics ready", de ? "Der Entwurf steht im Formular." : "The draft is in the form.")
            : (de ? "Songtext fehlgeschlagen" : "Lyrics failed", lyrics.Message ?? "");
    }

    public static (string Title, string Body) Test(string language) => IsGerman(language)
        ? ("Tonwerk", "Benachrichtigungen funktionieren.")
        : ("Tonwerk", "Notifications work.");

    private static bool IsGerman(string language) => language.StartsWith("de", StringComparison.OrdinalIgnoreCase);

    private static string WithMessage(string subject, string? message) =>
        string.IsNullOrWhiteSpace(message) ? subject : $"{subject}: {message}";

    private static string Duration(double seconds)
    {
        var total = (int)Math.Round(seconds);
        return $"{total / 60}:{total % 60:00}";
    }
}
