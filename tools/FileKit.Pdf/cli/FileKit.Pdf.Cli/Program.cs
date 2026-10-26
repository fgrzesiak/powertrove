using System.Globalization;
using FileKit.Pdf;

// Minimal command line for manual testing and standalone use. Never overwrites files.
const string Usage = """
Usage:
  filekit-pdf info <input.pdf>
  filekit-pdf merge <output.pdf> <input1.pdf> <input2.pdf> [...]
  filekit-pdf extract <input.pdf> <pages> <output.pdf>      pages: e.g. "1-3, 5"
  filekit-pdf remove <input.pdf> <pages> <output.pdf>
  filekit-pdf rotate <input.pdf> <90|180|270> <output.pdf> [pages]
  filekit-pdf split <input.pdf> <output-folder>
  filekit-pdf strip-metadata <input.pdf> <output.pdf>
  filekit-pdf images <output.pdf> <image1.jpg|png> [...]
""";

try
{
    if (args.Length == 0)
    {
        Console.WriteLine(Usage);
        return 1;
    }

    switch (args[0])
    {
        case "info" when args.Length == 2:
        {
            var document = PdfDocument.Open(args[1]);
            var info = PdfTools.GetInfo(document);
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"Pages: {info.PageCount}\nVersion: {info.Version}\nRepaired: {info.WasRepaired}\nFirst page: {info.FirstPageWidthPoints:0.#} x {info.FirstPageHeightPoints:0.#} pt"));
            return 0;
        }
        case "merge" when args.Length >= 4:
        {
            var documents = args.Skip(2).Select(path => PdfDocument.Open(path)).ToList();
            using var output = CreateNew(args[1]);
            PdfTools.Merge(documents, output);
            return 0;
        }
        case "extract" when args.Length == 4:
        {
            var document = PdfDocument.Open(args[1]);
            using var output = CreateNew(args[3]);
            PdfTools.ExtractPages(document, PageRange.Parse(args[2], document.PageCount), output);
            return 0;
        }
        case "remove" when args.Length == 4:
        {
            var document = PdfDocument.Open(args[1]);
            using var output = CreateNew(args[3]);
            PdfTools.RemovePages(document, PageRange.Parse(args[2], document.PageCount).ToList(), output);
            return 0;
        }
        case "rotate" when args.Length is 4 or 5:
        {
            var document = PdfDocument.Open(args[1]);
            int degrees = int.Parse(args[2], CultureInfo.InvariantCulture);
            var pages = args.Length == 5 ? PageRange.Parse(args[4], document.PageCount).ToList() : null;
            using var output = CreateNew(args[3]);
            PdfTools.Rotate(document, degrees, pages, output);
            return 0;
        }
        case "split" when args.Length == 3:
        {
            var document = PdfDocument.Open(args[1]);
            Directory.CreateDirectory(args[2]);
            string stem = Path.GetFileNameWithoutExtension(args[1]);
            PdfTools.Split(document, i => CreateNew(Path.Combine(args[2], $"{stem} - page {i + 1}.pdf")));
            return 0;
        }
        case "strip-metadata" when args.Length == 3:
        {
            var document = PdfDocument.Open(args[1]);
            using var output = CreateNew(args[2]);
            PdfTools.RemoveMetadata(document, output);
            return 0;
        }
        case "images" when args.Length >= 3:
        {
            var images = args.Skip(2).Select(path => PdfImage.Load(path)).ToList();
            using var output = CreateNew(args[1]);
            PdfTools.ImagesToPdf(images, output);
            return 0;
        }
        default:
            Console.WriteLine(Usage);
            return 1;
    }
}
catch (PdfException ex)
{
    Console.Error.WriteLine("Error: " + ex.Message);
    return 2;
}
catch (IOException ex)
{
    Console.Error.WriteLine("I/O error: " + ex.Message);
    return 3;
}

static FileStream CreateNew(string path) => new(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
