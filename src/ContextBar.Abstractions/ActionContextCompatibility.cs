using System;
using System.Collections.Generic;
using System.Linq;

namespace ContextBar.Abstractions;

/// <summary>
/// Common, fail-closed static selection and capability check for action search
/// and the central action registry. Does not execute a provider or action.
/// </summary>
public static class ActionContextCompatibility
{
    public static ActionAvailability Check(ContextSnapshot context, ActionDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(descriptor);

        // Context-free actions (they accept ContentKind.None, such as Settings) work for every
        // selection and also when the selection could not be captured.
        if (descriptor.Accepts.Contains(ContentKind.None))
            return new ActionAvailability(true);

        if (context.Status != ContextStatus.Available)
            return new ActionAvailability(false, ContextBar.Abstractions.Resources.SdkStrings.ContextUnavailable);

        ContentKind kind = context.Content switch
        {
            TableData => ContentKind.Table,
            FileSelection => ContentKind.Files,
            TextSelection => ContentKind.Text,
            _ => ContentKind.None
        };

        if (!descriptor.Accepts.Contains(kind) && !descriptor.Accepts.Contains(ContentKind.None))
            return new ActionAvailability(false,
                ContextBar.Abstractions.Resources.SdkStrings.RequiresType(string.Join(" or ", descriptor.Accepts), kind.ToString()));

        if (descriptor.RequiredCapabilities is { Count: > 0 })
        {
            // Explicit ordinal semantics: capability IDs are exact versioned tokens.
            var provided = context.ProvidedCapabilities is null
                ? new HashSet<string>(StringComparer.Ordinal)
                : new HashSet<string>(context.ProvidedCapabilities, StringComparer.Ordinal);
            if (descriptor.RequiredCapabilities.Any(required =>
                string.IsNullOrWhiteSpace(required) || !provided.Contains(required)))
            {
                return new ActionAvailability(false, ContextBar.Abstractions.Resources.SdkStrings.MissingRequiredCapabilities);
            }
        }

        if (descriptor.Selection is { } selection && context.Content is FileSelection fs)
        {
            if (selection.GetFamilyLimitProblem(fs) is string problem)
                return new ActionAvailability(false, problem);

            int matchingCount = 0;
            int otherCount = 0;
            string primaryFamilyName = string.Empty;

            foreach (var file in fs.Files)
            {
                string family = SelectionFamilies.Of(file);
                if (selection.Families.Count == 0 || selection.Families.Contains(family))
                {
                    matchingCount++;
                    if (primaryFamilyName.Length == 0) primaryFamilyName = selection.Families.Count > 0 ? family.ToUpperInvariant() : "item";
                }
                else
                {
                    otherCount++;
                }
            }

            if (string.IsNullOrEmpty(primaryFamilyName))
            {
                primaryFamilyName = selection.Families.FirstOrDefault()?.ToUpperInvariant() ?? "item";
            }

            if (matchingCount < selection.MinItems)
            {
                return new ActionAvailability(false, ContextBar.Abstractions.Resources.SdkStrings.RequiresMinItems(selection.MinItems, primaryFamilyName));
            }

            if (selection.MaxItems.HasValue && matchingCount > selection.MaxItems.Value)
            {
                return new ActionAvailability(false, ContextBar.Abstractions.Resources.SdkStrings.RequiresMaxItems(selection.MaxItems.Value, primaryFamilyName));
            }

            if (!selection.AllowOtherItems && otherCount > 0)
            {
                return new ActionAvailability(false, ContextBar.Abstractions.Resources.SdkStrings.RemoveOtherItems);
            }
        }

        return new ActionAvailability(true);
    }
}
