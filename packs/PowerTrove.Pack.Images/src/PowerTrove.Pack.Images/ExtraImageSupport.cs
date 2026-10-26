namespace PowerTrove.Pack.Images;

internal static class ExtraImageSupport
{
    public static string Text(string key) => ExtendedImageMessages.Get(key);
    public static string Count(string key, int count) => string.Format(Text(key + (count == 1 ? "_One" : "_Other")), count);
    public static ActionParameterDefinition Choice(string key, string defaultValue, params string[] values) => ActionParameterDefinition.Choice(key, Text(key), defaultValue,
        values.Select(v => new ActionParameterChoice(v, Text("Choice_" + v))).ToArray());
    public static ActionDescriptor Descriptor(string id, string key, IReadOnlyList<ActionParameterDefinition>? parameters = null, int min = 1, int max = 1000) => new(
        "contextbar.image." + id, Text("Title_" + key), Text("Description_" + key), new HashSet<ContentKind> { ContentKind.Files },
        new[] { "image", "photo", id }, true, true, Parameters: parameters,
        Selection: new SelectionRequirements(new HashSet<string> { "image" }, MinItems: min, MaxItems: max, IdealMinItems: min));
    public static IEnumerable<string> AnimationWarning(IReadOnlyList<SelectedFile> files)
    {
        if (files.Any(f => RasterSupport.Format(f) is FileFormats.Gif or FileFormats.WebP)) yield return SR.GetString("ImageExportAction_Warn_Animation");
    }
}
