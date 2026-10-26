using System.Text.Json.Serialization;

namespace PowerTrove.Core.Extensibility.PackProtocol;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(HelloMessage))]
[JsonSerializable(typeof(ReadyMessage))]
[JsonSerializable(typeof(PreviewMessage))]
[JsonSerializable(typeof(ExecuteMessage))]
[JsonSerializable(typeof(PreviewResultMessage))]
[JsonSerializable(typeof(ProgressMessage))]
[JsonSerializable(typeof(ResultMessage))]
[JsonSerializable(typeof(CancelMessage))]
[JsonSerializable(typeof(ErrorMessage))]
[JsonSerializable(typeof(HeartbeatMessage))]
[JsonSerializable(typeof(ShutdownMessage))]
internal partial class PackJsonContext : JsonSerializerContext { }
