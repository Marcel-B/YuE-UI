using System.Diagnostics;
using YueUI.Api.Export;
using YueUI.Api.Share;

namespace YueUI.Api.Tests;

/// <summary>Against real files made by ffmpeg, where there is one: whether players find the tags is what could break.</summary>
public sealed class TagLibTaggerTests : IDisposable
{
    /// <summary>The smallest valid PNG: one transparent pixel.</summary>
    private static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNkYAAAAAYAAjCB0C8AAAAASUVORK5CYII=");

    private readonly string _root = Directory.CreateTempSubdirectory("yueui-tagger-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Theory]
    [InlineData("mp3", "libmp3lame")]
    [InlineData("m4a", "aac")]
    [InlineData("flac", "flac")]
    public async Task Every_field_reads_back(string extension, string codec)
    {
        if (AacEncoder.FindFfmpeg() is not { } ffmpeg)
        {
            return; // Nothing to run against on a machine without ffmpeg; the Mac and CI's runner have it.
        }
        var path = Path.Combine(_root, $"song.{extension}");
        await Run(ffmpeg, "-nostdin", "-loglevel", "error", "-f", "lavfi", "-i", "sine=frequency=440:duration=1", "-c:a", codec, path);
        var tags = new SongTags(
            "Neon Nächte", "Marcel", "Neon Nächte", 2026, 2, 3, "synthwave", "[verse]\nÜber die Stadt", "YuE2 · Seed 42", new CoverImage(Png, "image/png"));

        new TagLibTagger().Write(path, tags);

        using var file = TagLib.File.Create(path);
        var tag = file.Tag;
        Assert.Equal("Neon Nächte", tag.Title);
        Assert.Equal(["Marcel"], tag.Performers);
        Assert.Equal("Neon Nächte", tag.Album);
        Assert.Equal((2026u, 2u, 3u), (tag.Year, tag.Track, tag.TrackCount));
        Assert.Equal(["synthwave"], tag.Genres);
        Assert.Equal("[verse]\nÜber die Stadt", tag.Lyrics?.Replace("\r\n", "\n").Replace('\r', '\n'));
        Assert.Equal("YuE2 · Seed 42", tag.Comment);
        var picture = Assert.Single(tag.Pictures);
        Assert.Equal(TagLib.PictureType.FrontCover, picture.Type);
        Assert.Equal("image/png", picture.MimeType);
        Assert.Equal(Png, picture.Data.Data);
        Assert.True(file.Properties.Duration > TimeSpan.FromSeconds(0.5));
        if (extension == "mp3")
        {
            Assert.Equal(3, ((TagLib.Id3v2.Tag)file.GetTag(TagLib.TagTypes.Id3v2)).Version);
        }
    }

    [Fact]
    public void A_file_that_is_no_audio_is_refused()
    {
        var path = Path.Combine(_root, "song.flac");
        File.WriteAllBytes(path, [.. "fLaC"u8, .. new byte[96]]);

        Assert.Throws<InvalidOperationException>(() =>
            new TagLibTagger().Write(path, new SongTags("x", null, null, null, null, null, null, null, null, null)));
    }

    private static async Task Run(string tool, params string[] arguments)
    {
        var start = new ProcessStartInfo(tool) { RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }
        using var process = Process.Start(start)!;
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, error);
    }
}
