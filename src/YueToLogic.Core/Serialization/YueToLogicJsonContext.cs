using System.Text.Json;
using System.Text.Json.Serialization;
using YueToLogic.Core.Abc;
using YueToLogic.Core.Conversion;
using YueToLogic.Core.Diagnostics;
using YueToLogic.Core.Model;
using YueToLogic.Core.Stems;

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
[JsonSerializable(typeof(Diagnostic[]))]
[JsonSerializable(typeof(StemJob))]
[JsonSerializable(typeof(StemJobResponse))]
[JsonSerializable(typeof(StemProblemDetails))]
public sealed partial class YueToLogicJsonContext : JsonSerializerContext;
