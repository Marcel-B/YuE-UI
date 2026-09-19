using Microsoft.Extensions.DependencyInjection.Extensions;
using YueToLogic.Core.Abc;
using YueToLogic.Core.Arrangement;
using YueToLogic.Core.Conversion;
using YueToLogic.Core.Midi;

// Placed in the DI namespace by convention so that AddYueToLogic() is found without an extra using.
namespace Microsoft.Extensions.DependencyInjection;

public static class YueToLogicServiceCollectionExtensions
{
    /// <summary>Registers the parser, arranger, MIDI renderer and converter as singletons; all of them are stateless.</summary>
    public static IServiceCollection AddYueToLogic(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<IAbcScoreParser, AbcScoreParser>();
        services.TryAddSingleton<IScoreArranger, ScoreArranger>();
        services.TryAddSingleton<IMidiRenderer, MidiRenderer>();
        services.TryAddSingleton<IScoreConverter, ScoreConverter>();
        return services;
    }
}
