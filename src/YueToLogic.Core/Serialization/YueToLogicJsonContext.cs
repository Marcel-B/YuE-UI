using System.Text.Json;
using System.Text.Json.Serialization;
using YueToLogic.Core.Abc;
using YueToLogic.Core.Conversion;
using YueToLogic.Core.Diagnostics;
using YueToLogic.Core.Logic;
using YueToLogic.Core.Model;
using YueToLogic.Core.Stems;
using YueToLogic.Core.Voices;

namespace YueToLogic.Core.Serialization;

/// <summary>
/// Source-generated JSON metadata (camelCase, enums as strings) for the public models, so web hosts and
/// trimmed/AOT builds can serialize results without reflection: <c>JsonSerializer.Serialize(result, YueToLogicJsonContext.Default.ConversionResult)</c>.
/// </summary>
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, WriteIndented = true, UseStringEnumConverter = true)]
[JsonSerializable(typeof(ConversionResult))]
[JsonSerializable(typeof(ConversionOptions))]
[JsonSerializable(typeof(AbcParseResult))]
[JsonSerializable(typeof(ScoreDocument))]
[JsonSerializable(typeof(MidiToAbcOptions))]
[JsonSerializable(typeof(MidiToAbcResult))]
[JsonSerializable(typeof(Diagnostic[]))]
[JsonSerializable(typeof(Dictionary<string, LogicInstrument>))]
[JsonSerializable(typeof(StemJob))]
[JsonSerializable(typeof(StemJob[]))]
[JsonSerializable(typeof(StemJobResponse))]
[JsonSerializable(typeof(StemJobResponse[]))]
[JsonSerializable(typeof(SeparationModel))]
[JsonSerializable(typeof(SeparationModel[]))]
[JsonSerializable(typeof(SeparationModelResponse))]
[JsonSerializable(typeof(SeparationModelResponse[]))]
[JsonSerializable(typeof(StemProblemDetails))]
[JsonSerializable(typeof(ReferenceVoice))]
[JsonSerializable(typeof(ReferenceVoice[]))]
[JsonSerializable(typeof(ReferenceVoiceResponse))]
[JsonSerializable(typeof(ReferenceVoiceResponse[]))]
[JsonSerializable(typeof(VoiceJob))]
[JsonSerializable(typeof(VoiceJob[]))]
[JsonSerializable(typeof(VoiceJobResponse))]
[JsonSerializable(typeof(VoiceJobResponse[]))]
[JsonSerializable(typeof(VoiceJobPage))]
[JsonSerializable(typeof(VoiceJobListResponse))]
[JsonSerializable(typeof(VoiceProblemDetails))]
public sealed partial class YueToLogicJsonContext : JsonSerializerContext;
