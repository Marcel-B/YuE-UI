using TagLib;

namespace YueUI.Api.Export;

/// <summary>A picture to embed as the front cover.</summary>
/// <param name="MimeType"><c>image/jpeg</c> or <c>image/png</c>, the two every player shows.</param>
public sealed record CoverImage(byte[] Data, string MimeType);

/// <summary>What an exported file says about its song. Null or empty fields are left out.</summary>
/// <param name="Album">The run's title; its songs are variants of one song, numbered by <paramref name="Track"/>.</param>
/// <param name="Comment">Style and seed, so the file still says how it was made.</param>
public sealed record SongTags(
    string Title,
    string? Artist,
    string? Album,
    int? Year,
    int? Track,
    int? TrackCount,
    string? Genre,
    string? Lyrics,
    string? Comment,
    CoverImage? Cover);

/// <summary>Writes <see cref="SongTags"/> into an MP3, M4A or FLAC file in place. Tests replace it.</summary>
public interface IAudioTagger
{
    /// <exception cref="InvalidOperationException">The file is not audio TagLib can read.</exception>
    void Write(string path, SongTags tags);
}

/// <summary>
/// TagLib# writes each format's own kind of tags: ID3v2 for MP3 (<c>USLT</c> lyrics, <c>APIC</c> cover), iTunes atoms
/// for M4A (<c>©lyr</c>, <c>covr</c>) and Vorbis comments plus a picture block for FLAC. ID3v2.3 rather than 2.4,
/// since Windows and some car radios still read only 2.3.
/// </summary>
public sealed class TagLibTagger : IAudioTagger
{
    static TagLibTagger()
    {
        TagLib.Id3v2.Tag.DefaultVersion = 3;
        TagLib.Id3v2.Tag.ForceDefaultVersion = true;
        // The lyrics frame's language; TagLib takes the server's culture, which is "ivl" under the invariant one.
        // "XXX" is ID3's "unknown": the lyrics may be English or German.
        TagLib.Id3v2.Tag.Language = "XXX";
    }

    public void Write(string path, SongTags tags)
    {
        try
        {
            using var file = TagLib.File.Create(path);
            // A FLAC from the worker may carry tags of its own; the export says only what is known here.
            file.RemoveTags(file.TagTypes & ~TagTypes.FlacMetadata);
            var tag = file.GetTag(file.MimeType switch
            {
                "taglib/mp3" => TagTypes.Id3v2,
                "taglib/m4a" => TagTypes.Apple,
                "taglib/flac" => TagTypes.Xiph,
                var other => throw new InvalidOperationException($"Cannot tag {other}."),
            }, create: true);
            tag.Title = tags.Title;
            tag.Performers = Some(tags.Artist);
            tag.AlbumArtists = Some(tags.Artist);
            tag.Album = Blank(tags.Album);
            tag.Year = (uint)(tags.Year ?? 0);
            tag.Track = (uint)(tags.Track ?? 0);
            tag.TrackCount = (uint)(tags.TrackCount ?? 0);
            tag.Genres = Some(tags.Genre);
            tag.Lyrics = Blank(tags.Lyrics);
            tag.Comment = Blank(tags.Comment);
            if (tags.Cover is { } cover)
            {
                // FLAC keeps pictures in blocks of its own, outside the Vorbis comment; file.Tag reaches both.
                file.Tag.Pictures =
                [
                    new Picture(new ByteVector(cover.Data)) { Type = PictureType.FrontCover, MimeType = cover.MimeType, Description = "Cover" },
                ];
            }
            file.Save();
        }
        catch (Exception exception) when (exception is CorruptFileException or UnsupportedFormatException)
        {
            throw new InvalidOperationException($"Could not tag the file: {exception.Message}", exception);
        }
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string[] Some(string? value) => Blank(value) is { } text ? [text] : [];
}
