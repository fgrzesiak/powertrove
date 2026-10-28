using System.Collections.Generic;
using PowerTrove.Abstractions.Ocr;
using PowerTrove.Abstractions.Packs;
using PowerTrove.Infrastructure.Files;
using PowerTrove.Pack.Files;
using PowerTrove.Pack.Pdf;
using PowerTrove.Platform.Windows.Imaging;

namespace PowerTrove.Desktop;

/// <summary>Compiled for the Base and Full profiles: Files and PDF.</summary>
public static partial class BuiltInActionPacks
{
    static partial void AddBasePackFactories(List<IPackFactory> factories, PackHostServices host)
    {
        host.Add<ITextRecognizer>(new WindowsTextRecognizer());
        host.Add<FileKit.Html.ILocalHtmlPdfPrinter>(new PowerTrove.Pack.Pdf.Windows.WindowsHtmlPdfRenderer(new PowerTrove.Pack.Pdf.Html.SafeHtmlMarkupValidator()));
        factories.InsertRange(0, new IPackFactory[] { new FilesPackFactory(), new PdfPackFactory(new WindowsTextRecognizer()) });
    }
}
