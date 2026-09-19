using System.Text.Json;
using System.Text.Json.Serialization;
using YueToLogic.Core.Abc;
using YueToLogic.Core.Conversion;
using YueToLogic.Core.Model;

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
public sealed partial class YueToLogicJsonContext : JsonSerializerContext;
