namespace YueToLogic.Core.Diagnostics;

/// <summary>Stable identifiers for <see cref="Diagnostic.Code"/>, usable as localization keys by frontends.</summary>
public static class DiagnosticCodes
{
    // Header and structure
    public const string MissingHeaderField = "YTL001";
    public const string InvalidFieldValue = "YTL002";
    public const string UnsupportedKey = "YTL003";
    public const string IgnoredField = "YTL004";
    public const string UndeclaredVoice = "YTL005";
    public const string TempoChangeIgnored = "YTL006";

    // Music lines
    public const string UnsupportedNotation = "YTL010";
    public const string NonNativeDuration = "YTL011";
    public const string BarLengthMismatch = "YTL012";
    public const string VoicesOutOfSync = "YTL013";
    public const string TieMismatch = "YTL014";
    public const string PitchOutOfRange = "YTL015";
    public const string MissingBarLine = "YTL016";
    public const string ConflictingChange = "YTL017";
    public const string ScoreTruncated = "YTL018";

    // Harmony
    public const string UnknownChord = "YTL020";
    public const string ChordOutsideVocal = "YTL021";
    public const string NoChords = "YTL022";

    // Arrangement
    public const string UnknownVoice = "YTL030";

    // Tempo
    public const string TempoFitted = "YTL060";
    public const string TempoNotFitted = "YTL061";

    // Logic Pro project
    public const string InvalidAudio = "YTL050";
    public const string UnsupportedSampleRate = "YTL051";
    public const string AudioLengthMismatch = "YTL052";
    public const string LogicTemplateLimitation = "YTL053";

    /// <summary>The stems of a separation could not be put into the project.</summary>
    public const string StemsUnavailable = "YTL054";

    /// <summary>An instrument names a MIDI output the Logic template does not know, so its track is not routed.</summary>
    public const string MidiPortUnknown = "YTL055";

    /// <summary>The converted vocals of a voice job could not be put into the project.</summary>
    public const string VoiceUnavailable = "YTL056";

    // Fatal
    public const string NoMusic = "YTL040";
    public const string TickResolution = "YTL041";
    public const string InvalidOption = "YTL042";
}
