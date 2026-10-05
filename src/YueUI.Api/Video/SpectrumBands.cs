using System.Diagnostics;
using System.Numerics;

namespace YueUI.Api.Video;

/// <summary>
/// The video's analyzer computed as the app's own does, so both look alike: Web Audio's <c>AnalyserNode</c> (8192
/// points, Blackman window, smoothing 0.75, -90 to -20 dB, see <c>createSpectrumAnalyser</c> in <c>spectrum.ts</c>),
/// the loudest bin of each log-spaced band from 30 Hz to 16 kHz (<c>readBands</c>), bars that rise at once and fall at
/// a steady pace (<c>SpectrumBars.vue</c>). ffmpeg's <c>showcqt</c>, which drew the bars before, is linear in loudness:
/// the highs, a few tens of decibels below the bass, stayed flat, and with twice the app's bars each one jumped more.
/// The result is one grey byte per bar and frame, which ffmpeg reads back as a raw video one pixel high.
/// </summary>
public sealed class SpectrumBands
{
    public const int SampleRate = 48000;
    public const int FftSize = 8192;
    private const double LowestHz = 30;
    private const double HighestHz = 16000;
    private const double MinDecibels = -90;
    private const double MaxDecibels = -20;

    /// <summary>
    /// The browser reads its analyser 60 times a second and smooths by 0.75 each time; at 30 frames a second the same
    /// time constant is 0.75 squared.
    /// </summary>
    private static readonly double Smoothing = Math.Pow(0.75, 60.0 / VideoLayout.FrameRate);

    /// <summary>The browser's bars fall 0.025 of their height a frame at 60 frames a second.</summary>
    private const double Fall = 0.025 * 60 / VideoLayout.FrameRate;

    /// <summary>Even silence keeps a sliver, as in the app (<c>capHeight</c>, a fortieth of the height).</summary>
    private const double Sliver = 1.0 / 40;

    private readonly int _bars;
    private readonly int _samplesPerFrame = SampleRate / VideoLayout.FrameRate;
    private readonly float[] _ring = new float[FftSize];
    private readonly double[] _window = new double[FftSize];
    private readonly double[] _smoothed = new double[FftSize / 2];
    private readonly Complex[] _fft = new Complex[FftSize];
    private readonly (int First, int Last)[] _bands;
    private readonly double[] _shown;
    private int _written;
    private int _sinceFrame;

    public SpectrumBands(int bars)
    {
        _bars = bars;
        _shown = new double[bars];
        for (var i = 0; i < FftSize; i++)
        {
            // Web Audio's Blackman window (alpha 0.16).
            var phase = 2 * Math.PI * i / FftSize;
            _window[i] = 0.42 - 0.5 * Math.Cos(phase) + 0.08 * Math.Cos(2 * phase);
        }
        var hzPerBin = (double)SampleRate / FftSize;
        var ratio = Math.Log(HighestHz / LowestHz);
        _bands = new (int, int)[bars];
        for (var band = 0; band < bars; band++)
        {
            var from = LowestHz * Math.Exp(ratio * band / bars);
            var to = LowestHz * Math.Exp(ratio * (band + 1) / bars);
            // The lowest bands are narrower than one bin; they take the bin they fall into.
            var first = (int)Math.Floor(from / hzPerBin);
            _bands[band] = (first, Math.Min(FftSize / 2 - 1, Math.Max(first, (int)Math.Ceiling(to / hzPerBin) - 1)));
        }
    }

    /// <summary>Takes mono samples at <see cref="SampleRate"/> and writes a row of bars for every frame they complete.</summary>
    public void Push(ReadOnlySpan<float> samples, Stream rows)
    {
        foreach (var sample in samples)
        {
            _ring[_written++ % FftSize] = sample;
            if (++_sinceFrame == _samplesPerFrame)
            {
                _sinceFrame = 0;
                WriteFrame(rows);
            }
        }
    }

    /// <summary>The row for the very start, before any sound: the slivers.</summary>
    public void WriteSilence(Stream rows)
    {
        Span<byte> row = stackalloc byte[_bars];
        row.Fill(Level(0));
        rows.Write(row);
    }

    private void WriteFrame(Stream rows)
    {
        // The last FftSize samples, oldest first, as the browser's analyser holds them.
        for (var i = 0; i < FftSize; i++)
        {
            _fft[i] = new Complex(_ring[(_written + i) % FftSize] * _window[i], 0);
        }
        Transform(_fft);
        for (var bin = 0; bin < _smoothed.Length; bin++)
        {
            _smoothed[bin] = Smoothing * _smoothed[bin] + (1 - Smoothing) * _fft[bin].Magnitude / FftSize;
        }
        Span<byte> row = stackalloc byte[_bars];
        for (var band = 0; band < _bars; band++)
        {
            var (first, last) = _bands[band];
            var loudest = 0.0;
            for (var bin = first; bin <= last; bin++)
            {
                loudest = Math.Max(loudest, _smoothed[bin]);
            }
            var decibels = 20 * Math.Log10(Math.Max(loudest, 1e-12));
            // getByteFrequencyData rounds down to whole bytes; the bar's height is that byte over 255.
            var target = Math.Floor(Math.Clamp((decibels - MinDecibels) / (MaxDecibels - MinDecibels), 0, 1) * 255) / 255;
            _shown[band] = target > _shown[band] ? target : Math.Max(target, _shown[band] - Fall);
            row[band] = Level(_shown[band]);
        }
        rows.Write(row);
    }

    private static byte Level(double height) => (byte)Math.Round(Math.Max(height, Sliver) * 255);

    /// <summary>In-place radix-2 FFT.</summary>
    private static void Transform(Complex[] data)
    {
        var n = data.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            var bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1)
            {
                j ^= bit;
            }
            j ^= bit;
            if (i < j)
            {
                (data[i], data[j]) = (data[j], data[i]);
            }
        }
        for (var length = 2; length <= n; length <<= 1)
        {
            var angle = -2 * Math.PI / length;
            var step = new Complex(Math.Cos(angle), Math.Sin(angle));
            for (var start = 0; start < n; start += length)
            {
                var w = Complex.One;
                for (var k = 0; k < length / 2; k++)
                {
                    var even = data[start + k];
                    var odd = data[start + k + length / 2] * w;
                    data[start + k] = even + odd;
                    data[start + k + length / 2] = even - odd;
                    w *= step;
                }
            }
        }
    }

    /// <summary>
    /// Decodes the song with ffmpeg to mono samples and writes its bars, one row per video frame, to
    /// <paramref name="path"/>.
    /// </summary>
    public static async Task WriteAsync(string ffmpeg, string audio, int bars, string path, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(ffmpeg)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in new[] { "-nostdin", "-loglevel", "error", "-i", audio, "-vn", "-ac", "1", "-ar", SampleRate.ToString(), "-f", "f32le", "pipe:1" })
        {
            start.ArgumentList.Add(argument);
        }
        using var process = Process.Start(start) ?? throw new VideoException("ffmpeg could not be started.");
        var errors = process.StandardError.ReadToEndAsync(cancellationToken);
        using var registration = cancellationToken.Register(() =>
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // Already gone.
            }
        });
        var analyzer = new SpectrumBands(bars);
        await using (var rows = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
        {
            var buffer = new byte[1 << 16];
            var carry = 0;
            var input = process.StandardOutput.BaseStream;
            using var memory = new MemoryStream();
            analyzer.WriteSilence(memory);
            int read;
            while ((read = await input.ReadAsync(buffer.AsMemory(carry), cancellationToken)) > 0)
            {
                var available = carry + read;
                var whole = available / sizeof(float) * sizeof(float);
                analyzer.Push(System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(buffer.AsSpan(0, whole)), memory);
                carry = available - whole;
                buffer.AsSpan(whole, carry).CopyTo(buffer);
                if (memory.Length > 1 << 15)
                {
                    await rows.WriteAsync(memory.GetBuffer().AsMemory(0, (int)memory.Length), cancellationToken);
                    memory.SetLength(0);
                }
            }
            await rows.WriteAsync(memory.GetBuffer().AsMemory(0, (int)memory.Length), cancellationToken);
        }
        await process.WaitForExitAsync(cancellationToken);
        if (process.ExitCode != 0)
        {
            var reason = (await errors).Trim();
            throw new VideoException(reason.Length > 0 ? reason : $"ffmpeg exited with {process.ExitCode} reading the audio.");
        }
    }
}
