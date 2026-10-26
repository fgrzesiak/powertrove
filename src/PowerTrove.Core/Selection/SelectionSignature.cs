using System;
using System.Collections.Generic;
using System.Linq;
using PowerTrove.Abstractions;

namespace PowerTrove.Core.Selection;

public enum SelectionFamily { Pdf, Image, Audio, Video, Subtitle, Archive, Document, Spreadsheet, Text, Folder, Other }

public enum CountBucket { One, Few, Several, Many }

public sealed record SelectionSignature(
    ContentKind Kind,
    CountBucket Count,
    IReadOnlyList<SelectionFamily> Families)
{
    public string Key
    {
        get
        {
            if (Kind == ContentKind.None) return "none";
            if (Kind == ContentKind.Table) return "table";
            if (Kind == ContentKind.Text) return "text";

            if (Kind == ContentKind.Files)
            {
                var joinedFamilies = string.Join("+", Families.Select(f => f.ToString().ToLowerInvariant()));
                var countString = Count.ToString().ToLowerInvariant();
                return $"files:{joinedFamilies}:{countString}";
            }

            return "none";
        }
    }

    public bool IsMixed => Families.Count > 1;

    public static SelectionSignature From(ContextSnapshot snapshot)
    {
        if (snapshot.Status != ContextStatus.Available)
        {
            return new SelectionSignature(ContentKind.None, CountBucket.One, Array.Empty<SelectionFamily>());
        }

        if (snapshot.Content is NoContent)
        {
            return new SelectionSignature(ContentKind.None, CountBucket.One, Array.Empty<SelectionFamily>());
        }

        if (snapshot.Content is TextSelection)
        {
            return new SelectionSignature(ContentKind.Text, CountBucket.One, Array.Empty<SelectionFamily>());
        }

        if (snapshot.Content is TableData)
        {
            return new SelectionSignature(ContentKind.Table, CountBucket.One, Array.Empty<SelectionFamily>());
        }

        if (snapshot.Content is FileSelection fileSelection)
        {
            var count = fileSelection.Files.Count;
            var bucket = count switch
            {
                1 => CountBucket.One,
                <= 5 => CountBucket.Few,
                <= 20 => CountBucket.Several,
                _ => CountBucket.Many
            };

            var distinctFamilies = new HashSet<SelectionFamily>();
            foreach (var file in fileSelection.Files)
            {
                var familyStr = SelectionFamilies.Of(file);
                if (Enum.TryParse<SelectionFamily>(familyStr, true, out var family))
                {
                    distinctFamilies.Add(family);
                }
            }

            var sortedFamilies = distinctFamilies.OrderBy(f => (int)f).ToList();

            return new SelectionSignature(ContentKind.Files, bucket, sortedFamilies);
        }

        return new SelectionSignature(ContentKind.None, CountBucket.One, Array.Empty<SelectionFamily>());
    }
}
