using System.Globalization;
using System.Security;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Watermarker.Core.Rendering;

namespace Watermarker.Core.Office;

/// <summary>
/// Watermark Word come forma WordArt (VML) ancorata nelle intestazioni.
///
/// Accorgimenti per renderlo più difficile da togliere senza alterare il documento:
///  - Va in TUTTE le intestazioni di tutte le sezioni (predefinita, prima pagina, pari),
///    creandole dove mancano: non basta cancellarlo da una sola intestazione.
///  - Non usa l'id "PowerPlusWaterMarkObject" che Word usa per riconoscere le proprie
///    filigrane: "Progettazione > Filigrana > Rimuovi filigrana" non lo trova.
///  - Ha i blocchi VML (o:lock) su selezione, posizione, rotazione e testo.
///  - Viene inserito come run nel primo paragrafo esistente dell'intestazione, senza
///    aggiungere paragrafi: l'altezza dell'intestazione e l'impaginazione non cambiano.
///  - È dietro al testo: selezione, modifica e ricerca nel corpo funzionano come prima.
///
/// Limite: chi apre l'intestazione e ne svuota il contenuto (o modifica l'XML) lo rimuove.
/// In formato .docx modificabile non esiste una protezione assoluta; la protezione vera
/// la fa Purview impedendo la modifica.
/// </summary>
public sealed class WordWatermarker : IWatermarker
{
    public IReadOnlyCollection<string> Extensions { get; } = [".docx", ".docm", ".dotx", ".dotm"];

    public WatermarkResult Apply(string inputPath, string outputPath, WatermarkOptions options)
    {
        File.Copy(inputPath, outputPath, overwrite: true);
        var warnings = new List<string>();

        using var doc = WordprocessingDocument.Open(outputPath, isEditable: true);
        var main = doc.MainDocumentPart ?? throw new InvalidDataException("Word document without a main part.");
        var body = main.Document?.Body ?? throw new InvalidDataException("Word document without a body.");

        EnsureHeaders(main, body);

        string family = FontLoader.FamilyName(options.Font);
        var pageSizes = PageSizesByHeader(main, body);
        int shapeNumber = Random.Shared.Next(10_000, 60_000);

        foreach (var headerPart in main.HeaderParts.ToList())
        {
            var size = pageSizes.GetValueOrDefault(main.GetIdOfPart(headerPart), (595.3, 841.9)); // A4
            InsertWatermark(headerPart, options, family, size, ref shapeNumber);
        }

        return new WatermarkResult(warnings);
    }

    /// <summary>
    /// Garantisce che ogni pagina abbia un'intestazione: se in una catena di sezioni manca
    /// un tipo di intestazione richiesto (predefinita, prima pagina, pari) la crea.
    /// Le sezioni senza intestazione propria ereditano quella precedente, quindi le
    /// creiamo solo dove l'ereditarietà non ne fornisce una.
    /// </summary>
    private static void EnsureHeaders(MainDocumentPart main, Body body)
    {
        var sections = body.Descendants<SectionProperties>().ToList();
        if (sections.Count == 0)
        {
            var sp = new SectionProperties();
            body.Append(sp);
            sections.Add(sp);
        }

        bool evenOdd = main.DocumentSettingsPart?.Settings?.GetFirstChild<EvenAndOddHeaders>() is { } eo
                       && (eo.Val is null || eo.Val.Value);

        bool haveDefault = false, haveFirst = false, haveEven = false;

        foreach (var sect in sections)
        {
            var refs = sect.Elements<HeaderReference>().ToList();
            bool Has(HeaderFooterValues t) =>
                refs.Any(r => (r.Type?.Value ?? HeaderFooterValues.Default).Equals(t));

            haveDefault |= Has(HeaderFooterValues.Default);
            if (!haveDefault) { AddHeader(main, sect, HeaderFooterValues.Default); haveDefault = true; }

            bool titlePage = sect.GetFirstChild<TitlePage>() is { } tp && (tp.Val is null || tp.Val.Value);
            if (titlePage)
            {
                haveFirst |= Has(HeaderFooterValues.First);
                if (!haveFirst) { AddHeader(main, sect, HeaderFooterValues.First); haveFirst = true; }
            }

            if (evenOdd)
            {
                haveEven |= Has(HeaderFooterValues.Even);
                if (!haveEven) { AddHeader(main, sect, HeaderFooterValues.Even); haveEven = true; }
            }
        }
    }

    private static void AddHeader(MainDocumentPart main, SectionProperties sect, HeaderFooterValues type)
    {
        var part = main.AddNewPart<HeaderPart>();
        part.Header = new Header(new Paragraph(
            new ParagraphProperties(new ParagraphStyleId { Val = "Header" })));
        part.Header.Save();

        // headerReference/footerReference devono stare all'inizio di sectPr.
        sect.PrependChild(new HeaderReference { Type = type, Id = main.GetIdOfPart(part) });
    }

    /// <summary>Dimensioni della pagina (in punti) delle sezioni che usano ciascuna intestazione.</summary>
    private static Dictionary<string, (double W, double H)> PageSizesByHeader(MainDocumentPart main, Body body)
    {
        var map = new Dictionary<string, (double, double)>();
        foreach (var sect in body.Descendants<SectionProperties>())
        {
            var pgSz = sect.GetFirstChild<PageSize>();
            if (pgSz?.Width?.Value is not { } w || pgSz.Height?.Value is not { } h) continue;
            foreach (var r in sect.Elements<HeaderReference>())
                if (r.Id?.Value is { } id) map.TryAdd(id, (w / 20.0, h / 20.0)); // twip → punti
        }
        return map;
    }

    private static void InsertWatermark(HeaderPart part, WatermarkOptions o, string family, (double W, double H) page, ref int shapeNumber)
    {
        var header = part.Header ?? throw new InvalidDataException("Unreadable header.");

        var paragraph = header.Elements<Paragraph>().FirstOrDefault()
                        ?? header.Descendants<Paragraph>().FirstOrDefault();
        if (paragraph is null)
        {
            paragraph = new Paragraph();
            header.Append(paragraph);
        }

        bool needsShapeType = !header.OuterXml.Contains("_x0000_t136", StringComparison.Ordinal);
        var lines = FontLoader.Lines(o.Text);
        var runs = new List<Run>();

        if (lines.Length == 1)
        {
            // Una riga: centrata nell'area dei margini, come le filigrane di Word.
            double width = FontLoader.MeasureWidth(lines[0], o.Font, o.FontSize);
            var style = "position:absolute;margin-left:0;margin-top:0;" +
                        $"width:{F(width)}pt;height:{F(o.FontSize)}pt;rotation:{F(o.ClockwiseDegrees)};z-index:-251650000;" +
                        "mso-position-horizontal:center;mso-position-horizontal-relative:margin;" +
                        "mso-position-vertical:center;mso-position-vertical-relative:margin";
            runs.Add(new Run(BuildRunXml(o, lines[0], style, "margin", family, shapeNumber++, needsShapeType)));
        }
        else
        {
            // Più righe: una forma per riga, posizionata sulla pagina. Il centro di ogni riga è spostato
            // perpendicolarmente al testo, così il blocco ruota come un'unica scritta.
            double a = o.AngleDegrees * Math.PI / 180;
            for (int i = 0; i < lines.Length; i++)
            {
                double width = FontLoader.MeasureWidth(lines[i], o.Font, o.FontSize);
                double v = FontLoader.LineOffset(i, lines.Length, o.FontSize);
                double cx = page.W / 2 + v * Math.Sin(a);
                double cy = page.H / 2 + v * Math.Cos(a);

                var style = "position:absolute;" +
                            $"margin-left:{F(cx - width / 2)}pt;margin-top:{F(cy - o.FontSize / 2)}pt;" +
                            $"width:{F(width)}pt;height:{F(o.FontSize)}pt;rotation:{F(o.ClockwiseDegrees)};z-index:-251650000;" +
                            "mso-position-horizontal-relative:page;mso-position-vertical-relative:page";
                runs.Add(new Run(BuildRunXml(o, lines[i], style, "page", family, shapeNumber++, needsShapeType && i == 0)));
            }
        }

        // Le forme vanno all'inizio del paragrafo (dopo le sue proprietà), nell'ordine delle righe.
        OpenXmlElement? anchor = paragraph.ParagraphProperties;
        foreach (var run in runs)
        {
            if (anchor is null) paragraph.PrependChild(run);
            else anchor.InsertAfterSelf(run);
            anchor = run;
        }

        header.Save();
    }

    private static string F(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);

    private static string BuildRunXml(WatermarkOptions o, string line, string style, string anchor,
                                      string family, int shapeNumber, bool withShapeType)
    {
        string text = SecurityElement.Escape(line);
        string font = SecurityElement.Escape(family);
        string shapeType = withShapeType ? ShapeType136 : "";

        return $"""
            <w:r xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"
                 xmlns:v="urn:schemas-microsoft-com:vml"
                 xmlns:o="urn:schemas-microsoft-com:office:office"
                 xmlns:w10="urn:schemas-microsoft-com:office:word">
              <w:rPr><w:noProof/></w:rPr>
              <w:pict>
                {shapeType}
                <v:shape id="_x0000_s{shapeNumber}" o:spid="_x0000_s{shapeNumber}" type="#_x0000_t136"
                  style="{style}"
                  o:allowincell="f" o:allowoverlap="t" fillcolor="#{o.Color.RgbHex}" stroked="f">
                  <v:fill opacity="{F(o.Color.Opacity)}"/>
                  <v:textpath style="font-family:&quot;{font}&quot;;font-size:1pt;font-weight:bold" string="{text}"/>
                  <o:lock v:ext="edit" selection="t" position="t" rotation="t" aspectratio="t" text="t" grouping="t"/>
                  <w10:wrap anchorx="{anchor}" anchory="{anchor}"/>
                </v:shape>
              </w:pict>
            </w:r>
            """;
    }

    /// <summary>Definizione standard della forma WordArt "testo semplice" (tipo 136).</summary>
    private const string ShapeType136 = """
        <v:shapetype id="_x0000_t136" coordsize="21600,21600" o:spt="136" adj="10800" path="m@7,l@8,m@5,21600l@6,21600e">
          <v:formulas>
            <v:f eqn="sum #0 0 10800"/>
            <v:f eqn="prod #0 2 1"/>
            <v:f eqn="sum 21600 0 @1"/>
            <v:f eqn="sum 0 0 @2"/>
            <v:f eqn="sum 21600 0 @3"/>
            <v:f eqn="if @0 @3 0"/>
            <v:f eqn="if @0 21600 @1"/>
            <v:f eqn="if @0 0 @2"/>
            <v:f eqn="if @0 @4 21600"/>
            <v:f eqn="mid @5 @6"/>
            <v:f eqn="mid @8 @5"/>
            <v:f eqn="mid @7 @8"/>
            <v:f eqn="mid @6 @7"/>
            <v:f eqn="sum @6 0 @5"/>
          </v:formulas>
          <v:path textpathok="t" o:connecttype="custom" o:connectlocs="@9,0;@10,10800;@11,21600;@12,10800" o:connectangles="270,180,90,0"/>
          <v:textpath on="t" fitshape="t"/>
          <v:handles><v:h position="#0,bottomRight" xrange="6629,14971"/></v:handles>
          <o:lock v:ext="edit" text="t" shapetype="t"/>
        </v:shapetype>
        """;
}
