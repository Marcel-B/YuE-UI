using System.Globalization;
using YueUI.Api.Share;
using YueUI.Api.Voices;

namespace YueUI.Api.Video;

/// <summary>
/// Animates the browser's still layers with ffmpeg's own audio visualizations and encodes what YouTube, Shorts, Reels
/// and TikTok recommend: H.264 High at 30 frames a second in yuv420p, AAC at 320 kbit/s and 48 kHz, the index at the
/// front (<c>faststart</c>). Only ffmpeg's built-in filters are used, so Homebrew's ffmpeg needs nothing extra (no
/// fonts: the title is a picture). One frame of a three-minute song is a still most of the way, so CRF 18 keeps it
/// sharp at a few tens of megabytes.
/// </summary>
public sealed class FfmpegVideoRenderer(ILogger<FfmpegVideoRenderer> logger) : IVideoRenderer
{
    /// <summary>A long song with particles takes a few minutes on the Mac mini; anything beyond this is hanging.</summary>
    private static readonly TimeSpan Timeout = TimeSpan.FromHours(2);

    /// <summary>The analyzer's colour, Tonwerk's emerald a shade lighter so it stands out on a dark background.</summary>
    public const string Accent = "0x34d399";

    /// <summary>How fast the particles drift up, in pixels a second.</summary>
    public const int ParticleSpeed = 40;

    public bool Available => AacEncoder.FindFfmpeg() is not null;

    public async Task RenderAsync(
        VideoState video,
        string flac,
        double? seconds,
        VideoLayers layers,
        string output,
        Action<double> progress,
        CancellationToken cancellationToken)
    {
        var ffmpeg = AacEncoder.FindFfmpeg() ?? throw new VideoException("ffmpeg is not installed.");
        var duration = await ProbeAsync(ffmpeg, flac, cancellationToken) ?? seconds;
        var temp = output + ".part.mp4";
        try
        {
            var result = await ToolProcess.RunAsync(
                ffmpeg,
                Arguments(video, flac, duration, layers, temp),
                Timeout,
                cancellationToken,
                line: line =>
                {
                    // -progress writes key=value lines; out_time_us is how far the output is (out_time_ms is too, despite its name).
                    if (duration is > 0 && line.StartsWith("out_time_us=", StringComparison.Ordinal)
                        && long.TryParse(line.AsSpan("out_time_us=".Length), NumberStyles.Integer, CultureInfo.InvariantCulture, out var micros))
                    {
                        progress(Math.Clamp(micros / 1e6 / duration.Value, 0, 1));
                    }
                });
            if (result.ExitCode != 0 || !File.Exists(temp))
            {
                var reason = ToolProcess.Reason(result, $"ffmpeg exited with {result.ExitCode}.");
                logger.LogWarning("Video {Id} failed: {Reason}", video.Id, string.Join(" | ", result.Tail));
                throw new VideoException(reason);
            }
            File.Move(temp, output, overwrite: true);
        }
        catch (VoiceServiceException exception)
        {
            throw new VideoException(exception.Message);
        }
        finally
        {
            File.Delete(temp);
        }
    }

    /// <summary>The whole command line; the graph is in <see cref="Graph"/>.</summary>
    public static IReadOnlyList<string> Arguments(VideoState video, string flac, double? seconds, VideoLayers layers, string output)
    {
        var rate = VideoLayout.FrameRate.ToString(CultureInfo.InvariantCulture);
        List<string> arguments = ["-nostdin", "-loglevel", "error", "-nostats", "-progress", "pipe:1", "-y"];
        // Each picture is read once; the graph repeats its frame (loop), so a PNG is not decoded thirty times a second.
        arguments.AddRange(["-framerate", rate, "-i", layers.Background]);
        arguments.AddRange(["-i", flac]);
        arguments.AddRange(["-framerate", rate, "-i", layers.Cover]);
        if (layers.Title is { } title)
        {
            arguments.AddRange(["-framerate", rate, "-i", title]);
        }
        if (layers.Particles is { } particles)
        {
            arguments.AddRange(["-framerate", rate, "-i", particles]);
        }
        arguments.AddRange(["-filter_complex", Graph(video, seconds, layers.Title is not null, layers.Particles is not null)]);
        arguments.AddRange(["-map", "[v]", "-map", "[a]"]);
        if (seconds is > 0)
        {
            arguments.AddRange(["-t", Number(seconds.Value)]);
        }
        // The pictures repeat forever; without a length the audio's end has to end the video.
        arguments.Add("-shortest");
        arguments.AddRange([
            "-c:v", "libx264", "-preset", "medium", "-crf", "18", "-profile:v", "high", "-pix_fmt", "yuv420p",
            "-r", rate, "-g", (VideoLayout.FrameRate * 2).ToString(CultureInfo.InvariantCulture),
            "-c:a", "aac", "-b:a", "320k", "-ar", "48000",
            "-movflags", "+faststart", "-f", "mp4", output,
        ]);
        return arguments;
    }

    /// <summary>
    /// Background, then particles, then the analyzer or waveform in its band, then the cover, then the title fading
    /// in; the whole fades in from and out to black. Inputs: 0 background, 1 audio, 2 cover, then title and particles
    /// when there are.
    /// </summary>
    public static string Graph(VideoState video, double? seconds, bool title, bool particles)
    {
        var layout = VideoLayout.For(video.Format);
        var (w, h, bandY, bandH) = (layout.Width, layout.Height, layout.BandY, layout.BandHeight);
        var rate = VideoLayout.FrameRate;
        var titleInput = 3;
        var particleInput = title ? 4 : 3;
        var effect = video.Effect is VideoEffects.Bars or VideoEffects.Wave;

        List<string> chains = [];
        List<string> audio = ["a"];
        if (effect)
        {
            audio.Add("ae");
        }
        if (particles)
        {
            audio.Add("ap");
        }
        chains.Add(audio.Count == 1 ? "[1:a]anull[a]" : $"[1:a]asplit={audio.Count}{string.Concat(audio.Select(a => $"[{a}]"))}");
        chains.Add("[0:v]format=rgba,loop=loop=-1:size=1[bg]");
        var current = "bg";

        if (particles)
        {
            // Two copies stacked and a window moving up through them: the browser drew the picture so that its top
            // and bottom edges meet.
            chains.Add($"[{particleInput}:v]format=rgba,loop=loop=-1:size=1,split[p1][p2]");
            chains.Add($"[p1][p2]vstack,crop={w}:{h}:0:'{h}-mod(t*{ParticleSpeed},{h})',split[pc][pt]");
            chains.Add("[pt]alphaextract[pa]");
            // The bass (30 to 250 Hz) as one brightness a frame: the share of the constant-Q bars that light up,
            // between 35 and 100 per cent, so the particles never vanish.
            chains.Add(
                $"[ap]showcqt=s=192x32:fps={rate}:sono_h=0:bar_h=32:axis=0:basefreq=30:endfreq=250,format=gray,"
                + "lut=y='if(gt(val,6),255,0)',scale=1:1:flags=area,lut=y='90+val*0.65',"
                + $"scale={w}:{h}:flags=neighbor[pulse]");
            chains.Add("[pa][pulse]blend=all_mode=multiply[pm]");
            chains.Add("[pc][pm]alphamerge[particles]");
            chains.Add($"[{current}][particles]overlay=0:0:format=auto[withparticles]");
            current = "withparticles";
        }

        if (effect)
        {
            if (video.Effect == VideoEffects.Bars)
            {
                // Constant-Q bars are spaced like the notes; thresholded, averaged per bar and thresholded again,
                // they become solid columns of the bar's width, cut apart by the gap mask.
                chains.Add(
                    $"[ae]showcqt=s={w}x{bandH}:fps={rate}:sono_h=0:bar_h={bandH}:axis=0:bar_g=2,format=gray,"
                    + $"lut=y='if(gt(val,6),255,0)',scale={w / VideoLayout.BarPitch}:{bandH}:flags=area,"
                    + $"scale={w}:{bandH}:flags=neighbor,lut=y='if(gt(val,110),255,0)'[bh]");
                chains.Add(
                    $"color=black:s={w}x{bandH}:r={rate}:d=1,format=gray,"
                    + $"geq=lum='if(lt(mod(X,{VideoLayout.BarPitch}),{VideoLayout.BarWidth}),255,0)',loop=loop=-1:size=1[bm]");
                chains.Add("[bh][bm]blend=all_mode=multiply[ba]");
            }
            else
            {
                chains.Add(
                    $"[ae]showwaves=s={w}x{bandH}:mode=cline:rate={rate}:scale=sqrt:draw=full:colors=white,format=gray,"
                    + "lut=y='if(gt(val,40),255,0)'[ba]");
            }
            chains.Add($"color={Accent}:s={w}x{bandH}:r={rate},format=rgba[bc]");
            chains.Add("[bc][ba]alphamerge[band]");
            chains.Add($"[{current}][band]overlay=0:{bandY}:format=auto[withband]");
            current = "withband";
        }

        chains.Add("[2:v]format=rgba,loop=loop=-1:size=1[cover]");
        chains.Add($"[{current}][cover]overlay=0:0:format=auto[withcover]");
        current = "withcover";

        if (title)
        {
            chains.Add($"[{titleInput}:v]format=rgba,loop=loop=-1:size=1,fade=t=in:st=0.8:d=1.2:alpha=1[title]");
            chains.Add($"[{current}][title]overlay=0:0:format=auto[withtitle]");
            current = "withtitle";
        }

        var fades = "fade=t=in:st=0:d=0.8";
        if (seconds is > 3)
        {
            fades += $",fade=t=out:st={Number(seconds.Value - 1.5)}:d=1.5";
        }
        chains.Add($"[{current}]{fades},format=yuv420p[v]");
        return string.Join(";", chains);
    }

    /// <summary>The FLAC's length from ffprobe, which lies next to ffmpeg; null when it cannot tell.</summary>
    private async Task<double?> ProbeAsync(string ffmpeg, string flac, CancellationToken cancellationToken)
    {
        var ffprobe = Path.Combine(Path.GetDirectoryName(ffmpeg)!, "ffprobe");
        if (!File.Exists(ffprobe))
        {
            return null;
        }
        try
        {
            var result = await ToolProcess.RunAsync(
                ffprobe,
                ["-v", "error", "-show_entries", "format=duration", "-of", "default=noprint_wrappers=1:nokey=1", flac],
                TimeSpan.FromMinutes(1),
                cancellationToken);
            return result.ExitCode == 0
                && double.TryParse(result.Output.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
                && seconds > 0
                    ? seconds
                    : null;
        }
        catch (VoiceServiceException exception)
        {
            logger.LogWarning("ffprobe failed on {Flac}: {Message}", flac, exception.Message);
            return null;
        }
    }

    private static string Number(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}
