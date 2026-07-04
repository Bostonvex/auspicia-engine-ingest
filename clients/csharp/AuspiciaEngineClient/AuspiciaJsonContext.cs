using System.Text.Json.Serialization;

namespace Auspicia.Engine;

[JsonSourceGenerationOptions(
    GenerationMode = JsonSourceGenerationMode.Metadata,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(EngineEnvelope))]
[JsonSerializable(typeof(EngineRun))]
[JsonSerializable(typeof(EngineJsonValue))]
[JsonSerializable(typeof(IngestResult))]
[JsonSerializable(typeof(ValidateResult))]
[JsonSerializable(typeof(ParameterListResult))]
[JsonSerializable(typeof(ParameterInfo))]
[JsonSerializable(typeof(ProblemDetails))]
[JsonSerializable(typeof(XrayBulkImportRequest))]
[JsonSerializable(typeof(XrayBulkImportResult))]
[JsonSerializable(typeof(XrayIngestionTargetsResult))]
[JsonSerializable(typeof(XrayAnalysisRequest))]
[JsonSerializable(typeof(XrayStartAnalysisResult))]
public partial class AuspiciaJsonContext : JsonSerializerContext
{
}
