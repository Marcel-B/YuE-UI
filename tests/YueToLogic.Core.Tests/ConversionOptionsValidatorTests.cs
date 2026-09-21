using YueToLogic.Core.Arrangement;
using YueToLogic.Core.Conversion;
using YueToLogic.Core.Diagnostics;
using static YueToLogic.Core.Tests.TestScores;

namespace YueToLogic.Core.Tests;

public class ConversionOptionsValidatorTests
{
    [Fact]
    public void Default_options_are_valid()
    {
        Assert.Empty(ConversionOptionsValidator.Validate(new ConversionOptions()));
    }

    [Fact]
    public void Out_of_range_values_are_reported_individually()
    {
        var options = new ConversionOptions
        {
            TicksPerQuarterNote = 10,
            Arrangement = new ArrangementOptions
            {
                DefaultOctaveShift = 5,
                OctaveShifts = new Dictionary<string, int> { ["Vocal"] = -9 },
                Bass = new BassOptions { OctaveShift = 3, Velocity = 0 },
            },
        };

        var errors = ConversionOptionsValidator.Validate(options);

        Assert.Equal(5, errors.Count);
        Assert.All(errors, e => Assert.Equal((DiagnosticSeverity.Error, DiagnosticCodes.InvalidOption), (e.Severity, e.Code)));
    }

    [Fact]
    public void The_new_arrangement_options_are_range_checked_as_well()
    {
        var options = new ConversionOptions
        {
            Arrangement = new ArrangementOptions
            {
                GuideTones = new GuideToneOptions { OctaveShift = 3, Velocity = 200 },
                Doubling = new DoublingOptions { VoiceId = " ", Semitones = 30 },
                Groove = new GrooveOptions { Swing = 1.5, HumanizeTimingMs = -1, HumanizeVelocity = 99, BaseVelocity = 0 },
                Mono = new MonoOptions { GapMs = 900, MinimumLengthMs = -5 },
            },
        };

        var errors = ConversionOptionsValidator.Validate(options);

        Assert.Equal(10, errors.Count);
        Assert.All(errors, e => Assert.Equal((DiagnosticSeverity.Error, DiagnosticCodes.InvalidOption), (e.Severity, e.Code)));
    }

    [Fact]
    public void The_defaults_of_the_new_options_pass()
    {
        var options = new ConversionOptions
        {
            Arrangement = new ArrangementOptions
            {
                Chords = new ChordOptions(),
                GuideTones = new GuideToneOptions(),
                Doubling = new DoublingOptions(),
                Groove = new GrooveOptions(),
                Mono = new MonoOptions(),
            },
        };

        Assert.Empty(ConversionOptionsValidator.Validate(options));
    }

    [Fact]
    public void Converter_rejects_invalid_options_without_throwing()
    {
        var result = new ScoreConverter().Convert(File.ReadAllText(SamplePath), new ConversionOptions { TicksPerQuarterNote = 0 });

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, d => d.Code == DiagnosticCodes.InvalidOption);
    }
}
