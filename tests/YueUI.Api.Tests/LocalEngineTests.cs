using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using YueUI.Api.Library;
using YueUI.Api.Share;
using YueUI.Api.Voices;

namespace YueUI.Api.Tests;

/// <summary>
/// The separator and Seed-VC run by this server itself, with stand-ins for mlx-audio-separator and Seed-VC's Python
/// (shell scripts that copy their input) and the real ffmpeg, which prepares and converts around them.
/// </summary>
public sealed class LocalEngineTests : IDisposable
{
    private const string Run = "20260921-165850-Neon-Night";

    private readonly TestApp _app = new();

    public void Dispose() => _app.Dispose();

    private string Engines => Path.Combine(_app.Root, "engines");

    private string SeparatorLog => Path.Combine(Engines, "separator.log");

    private string SeedVcLog => Path.Combine(Engines, "seed-vc.log");

    [Fact]
    public async Task The_server_separates_with_its_own_separator_and_names_the_stems_after_the_catalog()
    {
        if (AacEncoder.FindFfmpeg() is not { } ffmpeg)
        {
            return; // The preparation around the separator is ffmpeg's; the Mac and CI's runner have it.
        }
        InstallSeparator();
        _app.AddSong(Run, "song1");
        await Tone(ffmpeg, _app.AudioPath(Run, "song1"));
        var client = _app.CreateClient();

        var info = await client.GetFromJsonAsync<VoiceInfo>("/api/voice", TestApp.Json);
        Assert.True(info!.StemsConfigured);
        var models = await client.GetFromJsonAsync<StemModel[]>("/api/stems/models", TestApp.Json);
        Assert.Equal(27, models!.Length);
        Assert.Equal("mel-roformer-kim-vocals", models.Single(m => m.IsDefault).Id);

        await client.PostAsJsonAsync($"/api/songs/{Run}/song1/stems", new { dereverb = true });
        var done = await WaitForStems(client, s => s.Finished);

        Assert.True(done.Stage == "done", done.Message);
        // "(other)" is the model's name for the instrumental: the one stem left takes the one file left.
        Assert.Equal(["vocals", "instrumental", "vocals_dry", "vocals_reverb"], done.Stems.Select(s => s.Name));
        var calls = await File.ReadAllLinesAsync(SeparatorLog);
        Assert.Contains("--model_filename vocals_mel_band_roformer.ckpt", calls[0]);
        Assert.Contains($"--model_file_dir {Path.Combine(Engines, "separator", "models")}", calls[0]);
        Assert.Contains("--model_filename dereverb_mel_band_roformer_anvuew_sdr_19.1729.ckpt", calls[1]);
        Assert.Equal(0, _app.Stems.Calls);
    }

    [Fact]
    public void Pairing_stops_where_the_files_do_not_say_which_stem_they_are() =>
        Assert.Throws<VoiceServiceException>(() => MlxStemSeparator.MapStems(["vocals", "drums", "bass"], ["a_(Vocals).wav", "b_(x).wav", "c_(y).wav"]));

    [Fact]
    public async Task A_recording_becomes_a_voice_of_the_servers_own_and_sings_a_version()
    {
        if (AacEncoder.FindFfmpeg() is not { } ffmpeg)
        {
            return;
        }
        InstallSeparator();
        InstallSeedVc();
        _app.VoiceBaseUrl = null;
        _app.AddSong(Run, "song2");
        await Tone(ffmpeg, _app.AudioPath(Run, "song2"));
        var recording = Path.Combine(_app.Root, "recording.m4a");
        await Tone(ffmpeg, recording, seconds: 30);
        var client = _app.CreateClient();

        using var form = new MultipartFormDataContent
        {
            { new StringContent("Marcel"), "label" },
            { new StringContent("2"), "startSeconds" },
            { new StringContent("12"), "endSeconds" },
            { new ByteArrayContent(await File.ReadAllBytesAsync(recording)), "file", "recording.m4a" },
        };
        var added = await client.PostAsync("/api/voices", form);
        Assert.Equal(HttpStatusCode.Created, added.StatusCode);
        var voice = await added.Content.ReadFromJsonAsync<ReferenceVoice>(TestApp.Json);
        Assert.Equal(("Marcel", 10.0), (voice!.Label, voice.Seconds));
        Assert.Single((await client.GetFromJsonAsync<ReferenceVoice[]>("/api/voices", TestApp.Json))!);
        Assert.Equal("audio/wav", (await client.GetAsync($"/api/voices/{voice.Id}/audio")).Content.Headers.ContentType!.MediaType);

        await client.PostAsJsonAsync($"/api/songs/{Run}/song2/versions", new { voiceId = voice.Id, semiToneShift = -12, strength = 0.8, diffusionSteps = 30 });
        var done = await WaitForVersion(client, $"{Run}/song2", v => v.Finished);

        Assert.True(done.Stage == "done", done.Message);
        var call = (await File.ReadAllTextAsync(SeedVcLog)).Trim();
        Assert.Contains("--diffusion-steps 30 --inference-cfg-rate 0.8 --f0-condition --semi-tone-shift -12", call);
        Assert.Contains($"--reference {Path.Combine(_app.Root, "voices", voice.Id + ".wav")}", call);
        Assert.Contains($"SEED_VC_PATH={Path.Combine(Engines, "seed-vc", "src")}", call);
        Assert.Equal("converted.wav", Path.GetFileName(_app.Mixer.Input!.Vocals));
        Assert.Empty(_app.Voice.Requests);

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/voices/{voice.Id}")).StatusCode);
        Assert.False(File.Exists(Path.Combine(_app.Root, "voices", voice.Id + ".wav")));
    }

    [Fact]
    public async Task A_recording_too_short_to_carry_a_voice_is_refused()
    {
        if (AacEncoder.FindFfmpeg() is not { } ffmpeg)
        {
            return;
        }
        InstallSeedVc();
        var recording = Path.Combine(_app.Root, "short.wav");
        await Tone(ffmpeg, recording, seconds: 2);
        var client = _app.CreateClient();

        using var form = new MultipartFormDataContent
        {
            { new StringContent("Kurz"), "label" },
            { new ByteArrayContent(await File.ReadAllBytesAsync(recording)), "file", "short.wav" },
        };
        var response = await client.PostAsync("/api/voices", form);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.DoesNotContain((await client.GetFromJsonAsync<ReferenceVoice[]>("/api/voices", TestApp.Json))!, v => v.Label == "Kurz");
    }

    [Fact]
    public async Task ChangeMyVoices_voices_are_taken_over_once_with_their_ids()
    {
        InstallSeedVc();
        var client = _app.CreateClient();

        var voices = await client.GetFromJsonAsync<ReferenceVoice[]>("/api/voices", TestApp.Json);

        Assert.Equal(("v1", "Eurobecca", 24.5), (voices!.Single().Id, voices![0].Label, voices[0].Seconds));
        Assert.Equal([.. "RIFF"u8, 9], await File.ReadAllBytesAsync(Path.Combine(_app.Root, "voices", "v1.wav")));
        var asked = _app.Voice.Requests.Count;

        // Deleted here, it stays deleted: the takeover happened once.
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync("/api/voices/v1")).StatusCode);
        Assert.Empty((await client.GetFromJsonAsync<ReferenceVoice[]>("/api/voices", TestApp.Json))!);
        Assert.Equal(asked, _app.Voice.Requests.Count);
    }

    [Fact]
    public void The_estimate_follows_ChangeMyVoices_measurements() =>
        Assert.Equal((0, 666, 400), (SeedVcVoices.EstimatedSeconds(0, 50), SeedVcVoices.EstimatedSeconds(100, 50), SeedVcVoices.EstimatedSeconds(100, 30)));

    /// <summary>Writes a copy of its input per stem, named as mlx-audio-separator names them, and logs its arguments.</summary>
    private void InstallSeparator() => Script(
        Path.Combine(Engines, "separator", "env", "bin", "mlx-audio-separator"),
        $"""
        echo "$*" >> "{SeparatorLog}"
        input="$1"; shift
        while [ $# -gt 0 ]; do case "$1" in --output_dir) out="$2"; shift ;; --model_filename) model="$2"; shift ;; esac; shift; done
        case "$model" in
          dereverb*) cp "$input" "$out/song_(Vocals)_(noreverb).wav"; cp "$input" "$out/song_(Vocals)_(reverb).wav" ;;
          *) cp "$input" "$out/song_(Vocals).wav"; cp "$input" "$out/song_(other).wav" ;;
        esac
        """);

    /// <summary>Copies the source as the result and answers as yueui_seedvc.py does, logging its arguments and SEED_VC_PATH.</summary>
    private void InstallSeedVc()
    {
        Directory.CreateDirectory(Path.Combine(Engines, "seed-vc", "src"));
        File.WriteAllText(Path.Combine(Engines, "seed-vc", "src", "inference.py"), "");
        Script(
            Path.Combine(Engines, "seed-vc", "env", "bin", "python"),
            $$"""
            echo "$* SEED_VC_PATH=$SEED_VC_PATH" >> "{{SeedVcLog}}"
            while [ $# -gt 0 ]; do case "$1" in --source) source="$2"; shift ;; --output) output="$2"; shift ;; esac; shift; done
            cp "$source" "$output"
            echo "Seed-VC talks on its own"
            echo '{"status": "ok", "modelLoadMs": 1, "inferenceMs": 2}'
            """);
    }

    private static void Script(string path, string body)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "#!/bin/sh\n" + body + "\n");
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private static async Task Tone(string ffmpeg, string path, double seconds = 3)
    {
        var start = new ProcessStartInfo(ffmpeg) { RedirectStandardError = true };
        foreach (var argument in (string[])["-nostdin", "-loglevel", "error", "-y", "-f", "lavfi", "-i", $"sine=frequency=440:duration={seconds}", path])
        {
            start.ArgumentList.Add(argument);
        }
        using var process = Process.Start(start)!;
        await process.WaitForExitAsync();
        Assert.Equal(0, process.ExitCode);
    }

    private static async Task<StemSetState> WaitForStems(HttpClient client, Func<StemSetState, bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (true)
        {
            var sets = await client.GetFromJsonAsync<StemSetState[]>("/api/stems", TestApp.Json);
            if (sets?.FirstOrDefault() is { } set && condition(set))
            {
                return set;
            }
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"The stems never got there: {sets?.FirstOrDefault()?.Stage}");
            }
            await Task.Delay(50);
        }
    }

    private static async Task<VersionState> WaitForVersion(HttpClient client, string songId, Func<VersionState, bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (true)
        {
            var library = (await client.GetFromJsonAsync<RunInfo[]>("/api/library", TestApp.Json))!;
            if (library.SelectMany(r => r.Songs).Single(s => s.Id == songId).Versions.LastOrDefault() is { } version && condition(version))
            {
                return version;
            }
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("The version never got there.");
            }
            await Task.Delay(50);
        }
    }
}
