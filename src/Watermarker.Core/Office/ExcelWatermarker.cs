using System.Globalization;
using System.Text;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using Watermarker.Core.Rendering;

namespace Watermarker.Core.Office;

/// <summary>
/// Excel non ha una filigrana nativa. Per non interferire con le celle (una forma sopra
/// il foglio intercetterebbe i click) il watermark viene applicato in due modi:
///  - come immagine di sfondo del foglio, ripetuta a mosaico: visibile a schermo;
///  - come immagine nell'intestazione (&amp;G): visibile in stampa, in PDF e in Layout di pagina.
/// Entrambi sono applicati a ogni foglio di lavoro, quindi vanno rimossi foglio per foglio
/// e in due punti diversi. Celle, formule, filtri e selezione restano invariati.
/// </summary>
public sealed class ExcelWatermarker : IWatermarker
{
    // Ordine degli elementi figli di <worksheet> imposto dallo schema (CT_Worksheet).
    private static readonly string[] WorksheetOrder =
    [
        "sheetPr", "dimension", "sheetViews", "sheetFormatPr", "cols", "sheetData", "sheetCalcPr",
        "sheetProtection", "protectedRanges", "scenarios", "autoFilter", "sortState", "dataConsolidate",
        "customSheetViews", "mergeCells", "phoneticPr", "conditionalFormatting", "dataValidations",
        "hyperlinks", "printOptions", "pageMargins", "pageSetup", "headerFooter", "rowBreaks", "colBreaks",
        "customProperties", "cellWatches", "ignoredErrors", "smartTags", "drawing", "legacyDrawing",
        "legacyDrawingHF", "drawingHF", "picture", "oleObjects", "controls", "webPublishItems",
        "tableParts", "extLst",
    ];

    public IReadOnlyCollection<string> Extensions { get; } = [".xlsx", ".xlsm", ".xltx", ".xltm"];

    public WatermarkResult Apply(string inputPath, string outputPath, WatermarkOptions options)
    {
        File.Copy(inputPath, outputPath, overwrite: true);
        var warnings = new List<string>();

        var tile = WatermarkImage.Render(options, gapFactor: 2.5);           // sfondo a mosaico
        var single = WatermarkImage.Render(options, gapFactor: 0.3, dpi: 150); // stampa

        using var doc = SpreadsheetDocument.Open(outputPath, isEditable: true);
        var workbookPart = doc.WorkbookPart ?? throw new InvalidDataException("Invalid workbook.");

        var sheetNames = workbookPart.Workbook.Sheets?.Elements<Sheet>()
            .Where(s => s.Id?.Value is not null)
            .ToDictionary(s => s.Id!.Value!, s => s.Name?.Value ?? "?") ?? new();

        int index = 0;
        foreach (var wsPart in workbookPart.WorksheetParts.ToList())
        {
            index++;
            var name = sheetNames.GetValueOrDefault(workbookPart.GetIdOfPart(wsPart), $"#{index}");
            var ws = wsPart.Worksheet;

            AddBackground(wsPart, ws, tile, name, warnings);
            AddPrintHeaderImage(wsPart, ws, single, index, name, warnings);
            ws.Save();
        }

        if (index == 0) warnings.Add("No worksheets found.");
        return new WatermarkResult(warnings);
    }

    private static void AddBackground(WorksheetPart wsPart, Worksheet ws, RenderedImage img, string sheet, List<string> warnings)
    {
        if (ws.GetFirstChild<Picture>() is { } existing)
        {
            warnings.Add($"Sheet '{sheet}': the existing background image was replaced by the watermark.");
            if (existing.Id?.Value is { } oldId && wsPart.TryGetPartById(oldId, out var oldPart))
                wsPart.DeletePart(oldPart);
            existing.Remove();
        }

        var imagePart = wsPart.AddImagePart(ImagePartType.Png);
        using (var s = new MemoryStream(img.Png)) imagePart.FeedData(s);

        InsertInOrder(ws, new Picture { Id = wsPart.GetIdOfPart(imagePart) });
    }

    private static void AddPrintHeaderImage(WorksheetPart wsPart, Worksheet ws, RenderedImage img, int index, string sheet, List<string> warnings)
    {
        if (ws.GetFirstChild<LegacyDrawingHeaderFooter>() is not null)
        {
            warnings.Add($"Sheet '{sheet}': the header already contains images, print watermark not added.");
            return;
        }

        var vmlPart = wsPart.AddNewPart<VmlDrawingPart>();
        var imagePart = vmlPart.AddImagePart(ImagePartType.Png);
        using (var s = new MemoryStream(img.Png)) imagePart.FeedData(s);

        var vml = BuildHeaderVml(vmlPart.GetIdOfPart(imagePart), img, blockId: 900 + index);
        using (var s = new MemoryStream(Encoding.UTF8.GetBytes(vml))) vmlPart.FeedData(s);

        var hf = ws.GetFirstChild<HeaderFooter>();
        if (hf is null)
        {
            hf = new HeaderFooter();
            InsertInOrder(ws, hf);
        }

        hf.OddHeader = new OddHeader(AddPictureCode(hf.OddHeader?.Text));
        if (hf.DifferentOddEven?.Value == true)
            hf.EvenHeader = new EvenHeader(AddPictureCode(hf.EvenHeader?.Text));
        if (hf.DifferentFirst?.Value == true)
            hf.FirstHeader = new FirstHeader(AddPictureCode(hf.FirstHeader?.Text));

        InsertInOrder(ws, new LegacyDrawingHeaderFooter { Id = wsPart.GetIdOfPart(vmlPart) });
    }

    /// <summary>Inserisce il codice immagine &amp;G nella sezione centrale dell'intestazione, preservando il resto.</summary>
    private static string AddPictureCode(string? header)
    {
        if (string.IsNullOrEmpty(header)) return "&C&G";

        int c = header.IndexOf("&C", StringComparison.Ordinal);
        if (c >= 0) return header.Insert(c + 2, "&G");

        bool hasSections = header.Contains("&L", StringComparison.Ordinal) || header.Contains("&R", StringComparison.Ordinal);
        return hasSections ? header + "&C&G" : "&C&G" + header; // testo senza sezioni = centrale
    }

    private static string BuildHeaderVml(string imageRelId, RenderedImage img, int blockId)
    {
        string F(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);
        int spid = blockId * 1024 + 1;

        // L'id "CH" indica a Excel che l'immagine appartiene alla sezione centrale dell'intestazione.
        return $"""
            <xml xmlns:v="urn:schemas-microsoft-com:vml" xmlns:o="urn:schemas-microsoft-com:office:office" xmlns:x="urn:schemas-microsoft-com:office:excel">
             <o:shapelayout v:ext="edit"><o:idmap v:ext="edit" data="{blockId}"/></o:shapelayout>
             <v:shapetype id="_x0000_t75" coordsize="21600,21600" o:spt="75" o:preferrelative="t" path="m@4@5l@4@11@9@11@9@5xe" filled="f" stroked="f">
              <v:stroke joinstyle="miter"/>
              <v:formulas>
               <v:f eqn="if lineDrawn pixelLineWidth 0"/>
               <v:f eqn="sum @0 1 0"/>
               <v:f eqn="sum 0 0 @1"/>
               <v:f eqn="prod @2 1 2"/>
               <v:f eqn="prod @3 21600 pixelWidth"/>
               <v:f eqn="prod @3 21600 pixelHeight"/>
               <v:f eqn="sum @0 0 1"/>
               <v:f eqn="prod @6 1 2"/>
               <v:f eqn="prod @7 21600 pixelWidth"/>
               <v:f eqn="sum @8 21600 0"/>
               <v:f eqn="prod @7 21600 pixelHeight"/>
               <v:f eqn="sum @10 21600 0"/>
              </v:formulas>
              <v:path o:extrusionok="f" gradientshapeok="t" o:connecttype="rect"/>
              <o:lock v:ext="edit" aspectratio="t"/>
             </v:shapetype>
             <v:shape id="CH" o:spid="_x0000_s{spid}" type="#_x0000_t75" style="position:absolute;margin-left:0;margin-top:0;width:{F(img.WidthPt)}pt;height:{F(img.HeightPt)}pt;z-index:1">
              <v:imagedata o:relid="{imageRelId}" o:title="img"/>
              <o:lock v:ext="edit" rotation="t"/>
             </v:shape>
            </xml>
            """;
    }

    private static void InsertInOrder(Worksheet ws, OpenXmlElement element)
    {
        int target = Array.IndexOf(WorksheetOrder, element.LocalName);
        OpenXmlElement? after = null;
        foreach (var child in ws.ChildElements)
        {
            int i = Array.IndexOf(WorksheetOrder, child.LocalName);
            if (i >= 0 && i < target) after = child;
        }

        if (after is null) ws.PrependChild(element);
        else after.InsertAfterSelf(element);
    }
}
