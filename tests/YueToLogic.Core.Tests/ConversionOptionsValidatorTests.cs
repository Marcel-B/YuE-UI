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
    public void Converter_rejects_invalid_options_without_throwing()
    {
        var result = new ScoreConverter().Convert(File.ReadAllText(SamplePath), new ConversionOptions { TicksPerQuarterNote = 0 });

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, d => d.Code == DiagnosticCodes.InvalidOption);
    }
}
