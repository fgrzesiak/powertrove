using System.Text;

namespace PowerTrove.Pack.Data;

public sealed class DataPack : IActionPack
{
    public DataPack(FileActionServices services)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Actions = new IAction[]
        {
            new MergeTextAction(services), new DataFileAction(services, "to-utf8"), new DataFileAction(services, "line-endings"),
            new DataFileAction(services, "csv-to-json"), new DataFileAction(services, "csv-to-markdown"), new DataFileAction(services, "csv-to-sql"),
            new MergeCsvAction(services), new DataFileAction(services, "json-format"), new DataFileAction(services, "json-minify"),
            new ExcelAction(services, "xlsx-to-csv"), new ExcelAction(services, "xlsx-to-json"), new ExcelAction(services, "csv-to-xlsx"),
            new JsonValidateAction(services), new DataFileAction(services, "json-to-csv"), new JsonToExcelAction(services),
            new ContactAction(services, "vcf-to-csv"), new ContactAction(services, "csv-to-vcf"),
            new ContactAction(services, "vcf-to-xlsx"), new ContactAction(services, "xlsx-to-vcf"), new MergeContactsAction(services)
        };
    }
    public ActionPackInfo Info => Describe();
    internal static ActionPackInfo Describe() => new("powertrove.data", SR.Get("Pack"), PowerTrove.Packs.PackReleaseVersion.Read(typeof(DataPack).Assembly), "PowerTrove");
    public IReadOnlyList<IAction> Actions { get; }
}
