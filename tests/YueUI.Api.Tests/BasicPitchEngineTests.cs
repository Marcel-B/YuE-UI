using Melanchall.DryWetMidi.Core;
using Melanchall.DryWetMidi.Interaction;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using YueUI.Api.AudioMidi;

namespace YueUI.Api.Tests;

/// <summary>
/// Against the real Basic Pitch, where its environment is: the default location, or <c>YUEUI_BASICPITCH_PYTHON</c>
/// (a Python that <c>deploy/install-midi.sh</c> set up). Without one there is nothing to run against.
/// </summary>
public sealed class BasicPitchEngineTests : IDisposable
{
    /// <summary>C D E F G F E D C, half a second each: a scale a voice could sing.</summary>
    private static readonly int[] Melody = [60, 62, 64, 65, 67, 65, 64, 62, 60];

    private readonly string _directory = Directory.CreateTempSubdirectory("yueui-basicpitch-").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public async Task A_sung_scale_comes_back_note_for_note_on_the_grid_without_its_overtones()
    {
        if (Engine() is not { Installed: true } engine)
        {
            return;
        }
        var audio = Path.Combine(_directory, "vocals.wav");
        File.WriteAllBytes(audio, Voice(Melody, 0.5));
        var output = Path.Combine(_directory, "vocals.mid");

        var result = await engine.ConvertAsync(
            new AudioMidiJob(audio, output, "Vocal", new AudioMidiSettings(Mono: true, Quantize: true), 120, Voice: true), CancellationToken.None);

        var midi = MidiFile.Read(output);
        var map = midi.GetTempoMap();
        var notes = midi.GetNotes().ToList();
        Assert.Equal(Melody.Length, result.Notes);
        Assert.Equal(Melody, notes.Select(n => (int)n.NoteNumber));
        // At 120 bpm a half second is a quarter: every note starts on a beat.
        Assert.Equal(Enumerable.Range(0, Melody.Length).Select(i => i * 480L), notes.Select(n => n.Time * 480 / ((TicksPerQuarterNoteTimeDivision)map.TimeDivision).TicksPerQuarterNote));
        Assert.Equal(120, map.GetTempoAtTime(new MidiTimeSpan(0)).BeatsPerMinute, 1);
        Assert.Equal("Vocal", midi.GetTrackChunks().SelectMany(c => c.Events).OfType<SequenceTrackNameEvent>().Single(e => e.Text.Length > 0).Text);
    }

    [Fact]
    public async Task Without_one_note_at_a_time_the_overtones_stay()
    {
        if (Engine() is not { Installed: true } engine)
        {
            return;
        }
        var audio = Path.Combine(_directory, "vocals.wav");
        File.WriteAllBytes(audio, Voice(Melody, 0.5));

        var result = await engine.ConvertAsync(
            new AudioMidiJob(audio, Path.Combine(_directory, "poly.mid"), "Vocal", new AudioMidiSettings(Mono: false), 120, Voice: true), CancellationToken.None);

        Assert.True(result.Notes >= Melody.Length);
    }

    private static BasicPitchEngine? Engine()
    {
        var options = new AudioMidiOptions { Python = Environment.GetEnvironmentVariable("YUEUI_BASICPITCH_PYTHON") };
        return File.Exists(options.ResolvedPython)
            ? new BasicPitchEngine(Options.Create(options), NullLogger<BasicPitchEngine>.Instance)
            : null;
    }

    /// <summary>A voice-like tone per note: five harmonics, a little vibrato, soft edges, 44.1 kHz mono 16 bit.</summary>
    private static byte[] Voice(int[] notes, double seconds)
    {
        const int rate = 44100;
        var length = (int)(rate * seconds);
        var samples = new short[notes.Length * length];
        var phase = 0.0;
        for (var n = 0; n < notes.Length; n++)
        {
            var frequency = 440 * Math.Pow(2, (notes[n] - 69) / 12.0);
            for (var i = 0; i < length; i++)
            {
                var t = i / (double)rate;
                phase += 2 * Math.PI * frequency * (1 + 0.006 * Math.Sin(2 * Math.PI * 5.5 * t)) / rate;
                var tone = 0.0;
                for (var k = 1; k <= 5; k++)
                {
                    tone += Math.Sin(k * phase) / k;
                }
                var envelope = Math.Min(1, Math.Min(t / 0.03, (seconds - t) / 0.05));
                samples[n * length + i] = (short)(tone * 0.2 * envelope * short.MaxValue);
            }
        }
        var wav = new MemoryStream();
        using (var writer = new BinaryWriter(wav))
        {
            writer.Write("RIFF"u8);
            writer.Write(36 + samples.Length * 2);
            writer.Write("WAVEfmt "u8);
            writer.Write(16);
            writer.Write((short)1);
            writer.Write((short)1);
            writer.Write(rate);
            writer.Write(rate * 2);
            writer.Write((short)2);
            writer.Write((short)16);
            writer.Write("data"u8);
            writer.Write(samples.Length * 2);
            foreach (var sample in samples)
            {
                writer.Write(sample);
            }
        }
        return wav.ToArray();
    }
}
