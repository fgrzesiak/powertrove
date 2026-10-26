using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using ContextBar.Core;

namespace ContextBar.Core.Extensibility;

// Message boundaries

public record PluginRequest(Guid RequestId, string ActionId, string SerializedContext, string SerializedParameters, bool IsPreview);

public record PluginResponse(Guid RequestId, bool Success, string? SerializedReceipt, string? SerializedPreview, string? ErrorMessage);

[Obsolete("Prototype protocol. Use ContextBar.Core.Extensibility.PackProtocol.PackMessageSerializer for worker protocol v1.")]
public static class PluginMessageSerializer
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
        Converters = { new IntPtrConverter() }
    };

    public static string SerializeRequest(PluginRequest request) => JsonSerializer.Serialize(request, Options);
    public static string SerializeResponse(PluginResponse response) => JsonSerializer.Serialize(response, Options);
    public static PluginRequest? DeserializeRequest(string json) => JsonSerializer.Deserialize<PluginRequest>(json, Options);
    public static PluginResponse? DeserializeResponse(string json) => JsonSerializer.Deserialize<PluginResponse>(json, Options);

    public static string SerializeContext(ContextSnapshot context) => JsonSerializer.Serialize(context, Options);
    public static string SerializeParameters(ActionParameters parameters) => JsonSerializer.Serialize(parameters, Options);
}
