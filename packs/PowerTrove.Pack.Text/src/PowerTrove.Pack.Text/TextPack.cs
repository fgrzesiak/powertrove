namespace PowerTrove.Pack.Text;

/// <summary>Offline-only, bounded text transformations. No clipboard polling or raw text logging.</summary>
public sealed class TextPack : IActionPack
{
    public static readonly IReadOnlyList<string> Modes =
    [
        "trim", "trim-lines", "collapse-whitespace", "remove-empty-lines",
        "unique-lines", "sort-lines", "lowercase", "uppercase", "titlecase",
        "regex-replace", "url-encode", "url-decode", "html-encode", "html-decode",
        "base64-encode", "base64-decode", "json-escape"
    ];
    public TextPack() => Actions = Modes.Select(mode => (IAction)new TextTransformAction(mode)).ToArray();
    public ActionPackInfo Info => Describe();
    internal static ActionPackInfo Describe() => new("powertrove.text", SR.Get("Pack"), PowerTrove.Packs.PackReleaseVersion.Read(typeof(TextPack).Assembly), "PowerTrove");
    public IReadOnlyList<IAction> Actions { get; }
}
