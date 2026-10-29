namespace PowerTrove.Abstractions;

/// <summary>Explicit, ephemeral request for a rendered visual preview. Never store in preferences or execution parameters.</summary>
public static class ImagePreviewRequest
{
    public const string ParameterName = "PowerTrove.Preview.Image";
    public static bool IsRequested(ActionParameters parameters) => parameters is DictionaryParameters values &&
        values.Values.TryGetValue(ParameterName, out var value) && value == "true";
    public static DictionaryParameters Create(IReadOnlyDictionary<string, string> values) =>
        new(new Dictionary<string, string>(values) { [ParameterName] = "true" });
}
