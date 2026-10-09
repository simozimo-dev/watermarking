using System.Globalization;
using System.Xml.Linq;
using Watermarker.Core.Packaging;
using Watermarker.Core.Rendering;

namespace Watermarker.Core.Visio;

/// <summary>
/// Watermark per Visio (.vsdx, .vsdm, .vstx, .vstm): una forma di solo testo su ogni pagina
/// di primo piano, centrata e ruotata, senza linea né riempimento.
///
///  - Ha tutti i blocchi ShapeSheet: selezione, spostamento, dimensioni, rotazione,
///    eliminazione, modifica del testo e della formattazione.
///  - È la prima forma della pagina, quindi sta dietro al diagramma: le forme esistenti
///    restano selezionabili e modificabili.
///  - La trasparenza del testo è impostata nella sezione Character (ColorTrans).
///
/// Limite: chi apre lo ShapeSheet può sbloccare la forma ed eliminarla.
/// </summary>
public sealed class VisioWatermarker : IWatermarker
{
    private static readonly XNamespace V = "http://schemas.microsoft.com/office/visio/2012/main";
    private static readonly XNamespace R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly XNamespace PkgRel = "http://schemas.openxmlformats.org/package/2006/relationships";

    private const string PagesPart = "visio/pages/pages.xml";

    public IReadOnlyCollection<string> Extensions { get; } = [".vsdx", ".vsdm", ".vstx", ".vstm"];

    public WatermarkResult Apply(string inputPath, string outputPath, WatermarkOptions options)
    {
        var pkg = ZipPackage.Load(inputPath);
        var warnings = new List<string>();

        if (!pkg.Contains(PagesPart))
            throw new InvalidDataException("The Visio file contains no pages.");

        var pages = pkg.GetXml(PagesPart);
        var rels = LoadRelationships(pkg, ZipPackage.RelsPathFor(PagesPart));

        // Dimensioni del testo in pollici (unità interna di Visio).
        double sizeIn = options.FontSize / 72.0;
        double widthIn = FontLoader.MeasureWidth(options.Text, options.Font, options.FontSize) / 72.0 * 1.15;
        var lines = FontLoader.Lines(options.Text);
        double heightIn = sizeIn * (1.6 + (lines.Length - 1) * FontLoader.LineSpacing);

        int done = 0;
        foreach (var page in pages.Root!.Elements(V + "Page"))
        {
            if ((string?)page.Attribute("Background") == "1") continue; // le pagine di sfondo le salta

            var relId = (string?)page.Element(V + "Rel")?.Attribute(R + "id");
            if (relId is null || !rels.TryGetValue(relId, out var target)) continue;
            var partName = ZipPackage.ResolveRelative(PagesPart, target);
            if (!pkg.Contains(partName)) continue;

            var sheet = page.Element(V + "PageSheet");
            double pageW = CellValue(sheet, "PageWidth") ?? 8.5;
            double pageH = CellValue(sheet, "PageHeight") ?? 11;
            if (sheet is null || CellValue(sheet, "PageWidth") is null)
                warnings.Add($"Page '{(string?)page.Attribute("NameU")}': size not specified, using Letter.");

            var content = pkg.GetXml(partName);
            var root = content.Root!;
            var shapes = root.Element(V + "Shapes");
            if (shapes is null)
            {
                shapes = new XElement(V + "Shapes");
                root.AddFirst(shapes);
            }

            int nextId = root.Descendants(V + "Shape")
                .Select(s => int.TryParse((string?)s.Attribute("ID"), out var id) ? id : 0)
                .DefaultIfEmpty(0).Max() + 1;

            shapes.AddFirst(CreateShape(nextId, pageW / 2, pageH / 2, widthIn, heightIn, sizeIn, options));
            pkg.SetXml(partName, content);
            done++;
        }

        if (done == 0) warnings.Add("No foreground pages found: watermark not applied.");

        pkg.Save(outputPath);
        return new WatermarkResult(warnings);
    }

    private static XElement CreateShape(int id, double pinX, double pinY, double w, double h, double sizeIn, WatermarkOptions o)
    {
        XElement Cell(string name, object value, string? unit = null)
        {
            var c = new XElement(V + "Cell", new XAttribute("N", name), new XAttribute("V", Format(value)));
            if (unit is not null) c.Add(new XAttribute("U", unit));
            return c;
        }

        return new XElement(V + "Shape",
            new XAttribute("ID", id),
            new XAttribute("NameU", $"Object {id}"),
            new XAttribute("Name", $"Object {id}"),
            new XAttribute("Type", "Shape"),
            new XAttribute("LineStyle", "0"),
            new XAttribute("FillStyle", "0"),
            new XAttribute("TextStyle", "0"),
            Cell("PinX", pinX), Cell("PinY", pinY),
            Cell("Width", w), Cell("Height", h),
            Cell("LocPinX", w / 2), Cell("LocPinY", h / 2),
            Cell("Angle", o.AngleDegrees * Math.PI / 180),
            Cell("LeftMargin", 0), Cell("RightMargin", 0), Cell("TopMargin", 0), Cell("BottomMargin", 0),
            Cell("VerticalAlign", 1),
            Cell("LockSelect", 1), Cell("LockMoveX", 1), Cell("LockMoveY", 1),
            Cell("LockWidth", 1), Cell("LockHeight", 1), Cell("LockRotate", 1),
            Cell("LockDelete", 1), Cell("LockTextEdit", 1), Cell("LockFormat", 1),
            Cell("LockGroup", 1), Cell("LockCustProp", 1),
            new XElement(V + "Section", new XAttribute("N", "Character"),
                new XElement(V + "Row", new XAttribute("IX", "0"),
                    Cell("Color", "#" + o.Color.RgbHex),
                    Cell("ColorTrans", 1 - o.Color.Opacity),
                    Cell("Size", sizeIn, "PT"),
                    Cell("Style", 1))),
            new XElement(V + "Section", new XAttribute("N", "Paragraph"),
                new XElement(V + "Row", new XAttribute("IX", "0"), Cell("HorzAlign", 1))),
            new XElement(V + "Text", string.Join("\n", FontLoader.Lines(o.Text))));
    }

    private static Dictionary<string, string> LoadRelationships(ZipPackage pkg, string relsPath)
    {
        var map = new Dictionary<string, string>();
        if (!pkg.Contains(relsPath)) return map;
        foreach (var rel in pkg.GetXml(relsPath).Root!.Elements(PkgRel + "Relationship"))
            if (rel.Attribute("Id")?.Value is { } id && rel.Attribute("Target")?.Value is { } target)
                map[id] = target;
        return map;
    }

    private static double? CellValue(XElement? sheet, string name)
    {
        var v = (string?)sheet?.Elements(V + "Cell").FirstOrDefault(c => (string?)c.Attribute("N") == name)?.Attribute("V");
        return double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;
    }

    private static string Format(object value) => value switch
    {
        double d => d.ToString("0.##########", CultureInfo.InvariantCulture),
        int i => i.ToString(CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "",
    };
}
