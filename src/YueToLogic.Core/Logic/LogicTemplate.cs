namespace YueToLogic.Core.Logic;

/// <summary>
/// The files of a Logic Pro package (<c>.logicx</c>) that <see cref="LogicProjectWriter"/> fills in, without its audio.
/// </summary>
/// <remarks>
/// The template must contain the MIDI regions <c>Vocal</c>, <c>Ins</c>, <c>Chords</c>, <c>Bass</c> and <c>Drums</c>
/// and an audio region for <c>Media/Audio Files/audio.flac</c> — i.e. a project created by opening a MIDI file
/// from this tool in Logic and adding the YuE audio at bar 1. The instruments chosen there are used for every
/// generated project. <c>MetaData.plist</c> and <c>ProjectInformation.plist</c> are expected as XML property lists.
/// </remarks>
public sealed class LogicTemplate
{
    public const string ProjectDataPath = "Alternatives/000/ProjectData";
    public const string MetaDataPath = "Alternatives/000/MetaData.plist";
    public const string ProjectInformationPath = "Resources/ProjectInformation.plist";
    public const string AudioPath = "Media/Audio Files/audio.flac";

    private static readonly Dictionary<string, string> EmbeddedFiles = new(StringComparer.Ordinal)
    {
        ["ProjectData"] = ProjectDataPath,
        ["MetaData.plist"] = MetaDataPath,
        ["DisplayState.plist"] = "Alternatives/000/DisplayState.plist",
        ["DisplayStateArchive"] = "Alternatives/000/DisplayStateArchive",
        ["ProjectInformation.plist"] = ProjectInformationPath,
    };

    private static readonly Lazy<LogicTemplate> BuiltIn = new(LoadEmbedded);

    public LogicTemplate(IReadOnlyDictionary<string, byte[]> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        foreach (var required in new[] { ProjectDataPath, MetaDataPath, ProjectInformationPath })
        {
            if (!files.ContainsKey(required))
            {
                throw new ArgumentException($"The Logic template lacks '{required}'.", nameof(files));
            }
        }

        Files = files;
    }

    /// <summary>The template shipped with this library (six tracks: audio, Vocal, Ins, Chords, Bass, Drums).</summary>
    public static LogicTemplate Default => BuiltIn.Value;

    /// <summary>Package-relative path ('/' separators) → content.</summary>
    public IReadOnlyDictionary<string, byte[]> Files { get; }

    private static LogicTemplate LoadEmbedded()
    {
        var assembly = typeof(LogicTemplate).Assembly;
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var (resource, path) in EmbeddedFiles)
        {
            using var stream = assembly.GetManifestResourceStream($"YueToLogic.LogicTemplate.{resource}")
                ?? throw new InvalidOperationException($"Embedded Logic template file '{resource}' is missing.");
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            files[path] = buffer.ToArray();
        }

        return new LogicTemplate(files);
    }
}
