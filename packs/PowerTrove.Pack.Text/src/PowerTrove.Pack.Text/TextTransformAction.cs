using System.Text;
using System.Text.RegularExpressions;

namespace PowerTrove.Pack.Text;

public sealed class TextTransformAction : IAction
{
    private readonly string _mode;
    public TextTransformAction(string mode)
    {
        if (!TextPack.Modes.Contains(mode, StringComparer.Ordinal))
            throw new ArgumentOutOfRangeException(nameof(mode));
        _mode = mode;
    }

    public ActionDescriptor Descriptor => new(
        "powertrove.text." + _mode,
        SR.Get(_mode),
        SR.Get("Description"),
        new HashSet<ContentKind> { ContentKind.Text },
        new[] { "text", "clipboard", _mode.Replace('-', ' ') },
        RequiresPreview: true, ChangesFiles: false,
        Parameters: _mode == "regex-replace" ?
            new ActionParameterDefinition[]
            {
                ActionParameterDefinition.Text("pattern", SR.Get("Pattern"), "", SR.Get("RegexHint")),
                ActionParameterDefinition.Text("replacement", SR.Get("Replacement"), "")
            } : null,
        CanRememberParameters: false);

    public ValueTask<ActionAvailability> MatchAsync(ContextSnapshot context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var fit = ActionContextCompatibility.Check(context, Descriptor);
        if (!fit.IsAvailable) return ValueTask.FromResult(fit);
        bool available = !context.IsStale &&
            context.Content is TextSelection { Text.Length: > 0 and <= TextTransforms.MaximumCharacters };
        return ValueTask.FromResult(new ActionAvailability(available, available ? null : SR.Get("Unavailable")));
    }

    public ValueTask<ActionPreview> PreviewAsync(ContextSnapshot context, ActionParameters parameters, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!CanRun(context)) return ValueTask.FromResult(Blocked(SR.Get("Unavailable")));
        try
        {
            string result = Transform(((TextSelection)context.Content).Text, parameters);
            return ValueTask.FromResult(new ActionPreview(
                RequiresConfirmation: true,
                PreviewText: SR.Get("Preview") + "\n" +
                    (result.Length <= 512 ? result : result[..512] + "…"),
                ExpectedOutputs: Array.Empty<string>(),
                Warnings: result.Length == 0 ? new[] { SR.Get("EmptyResult") } : Array.Empty<string>()));
        }
        catch (Exception ex) when (ex is ArgumentException or RegexMatchTimeoutException or FormatException or DecoderFallbackException)
        {
            return ValueTask.FromResult(Blocked(Problem(ex)));
        }
    }

    public Task<ActionReceipt> ExecuteAsync(ContextSnapshot context, ActionParameters parameters,
        IProgress<ActionProgress>? progress, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
            return Task.FromResult(new ActionReceipt(false, true, [], [], false));
        if (!CanRun(context))
            return Task.FromResult(Failure(SR.Get("Unavailable")));
        try
        {
            string result = Transform(((TextSelection)context.Content).Text, parameters);
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new ActionReceipt(true, false, [], [], false, SR.Get("Done"), ClipboardText: result));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Task.FromResult(new ActionReceipt(false, true, [], [], false));
        }
        catch (Exception ex) when (ex is ArgumentException or RegexMatchTimeoutException or FormatException or DecoderFallbackException)
        {
            return Task.FromResult(Failure(Problem(ex)));
        }
    }

    private string Transform(string text, ActionParameters parameters)
    {
        var values = ActionParameterValues.From(parameters, Descriptor.Parameters);
        return TextTransforms.Apply(_mode, text, _mode == "regex-replace" ? values.GetText("pattern") : "",
            _mode == "regex-replace" ? values.GetText("replacement") : "");
    }

    private static bool CanRun(ContextSnapshot context) =>
        context.Status == ContextStatus.Available && !context.IsStale &&
        context.Content is TextSelection { Text.Length: > 0 and <= TextTransforms.MaximumCharacters };

    private static ActionPreview Blocked(string message) => new(false, message, [], [], IsBlocked: true);
    private static ActionReceipt Failure(string message) =>
        new(false, false, [], [new ActionFailure(null, message)], false, message);
    private static string Problem(Exception exception) => exception is RegexMatchTimeoutException
        ? SR.Get("RegexTimeout") : exception is FormatException or DecoderFallbackException
            ? SR.Get("InvalidEncoding") : exception is ArgumentException argument && argument.Message == SR.Get("TooLarge")
                ? SR.Get("TooLarge") : SR.Get("InvalidInput");
}
