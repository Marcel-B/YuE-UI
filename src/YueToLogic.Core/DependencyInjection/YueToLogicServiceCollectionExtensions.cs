using Microsoft.Extensions.DependencyInjection.Extensions;
using YueToLogic.Core.Abc;
using YueToLogic.Core.Arrangement;
using YueToLogic.Core.Conversion;
using YueToLogic.Core.Logic;
using YueToLogic.Core.Midi;

// Placed in the DI namespace by convention so that AddYueToLogic() is found without an extra using.
namespace Microsoft.Extensions.DependencyInjection;

public static class YueToLogicServiceCollectionExtensions
{
    /// <summary>
    /// Registers the parser, arranger, MIDI renderer, converter, Logic project writer and the way back from MIDI to
    /// ABC as singletons; all of them are stateless.
    /// </summary>
    public static IServiceCollection AddYueToLogic(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<IAbcScoreParser, AbcScoreParser>();
        services.TryAddSingleton<IScoreArranger, ScoreArranger>();
        services.TryAddSingleton<IMidiRenderer, MidiRenderer>();
        services.TryAddSingleton<IScoreConverter, ScoreConverter>();
        services.TryAddSingleton<ILogicProjectWriter>(_ => new LogicProjectWriter());
        services.TryAddSingleton<IAbcScoreWriter, AbcScoreWriter>();
        services.TryAddSingleton<IMidiToAbcConverter, MidiToAbcConverter>();
        return services;
    }
}
