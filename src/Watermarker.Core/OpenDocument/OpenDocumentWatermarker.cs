using System.Globalization;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Watermarker.Core.Packaging;
using Watermarker.Core.Rendering;

namespace Watermarker.Core.OpenDocument;

/// <summary>
/// Watermark per i file OpenDocument (LibreOffice, OpenOffice) usando una forma Fontwork,
/// la stessa tecnica con cui LibreOffice importa le filigrane di Word.
///
///  - Testo (.odt/.ott): la forma è ancorata nell'intestazione di ogni stile di pagina, come in Word.
///    Se una pagina non ha intestazione ne viene creata una alta un punto, quasi senza effetto sull'impaginazione.
///  - Presentazioni (.odp/.otp): la forma è sulle pagine master, quindi dietro al contenuto di ogni slide.
///  - Fogli (.ods/.ots): le forme sono nel livello di sfondo di ogni foglio, ripetute sull'area usata,
///    dietro alle celle: selezione e modifica delle celle restano invariate.
///
/// Tutte le forme hanno posizione e dimensione protette e scorrono dietro al testo.
/// </summary>
public sealed class OpenDocumentWatermarker : IWatermarker
{
    private static readonly XNamespace Office = "urn:oasis:names:tc:opendocument:xmlns:office:1.0";
    private static readonly XNamespace Style = "urn:oasis:names:tc:opendocument:xmlns:style:1.0";
    private static readonly XNamespace Text = "urn:oasis:names:tc:opendocument:xmlns:text:1.0";
    private static readonly XNamespace Draw = "urn:oasis:names:tc:opendocument:xmlns:drawing:1.0";
    private static readonly XNamespace Svg = "urn:oasis:names:tc:opendocument:xmlns:svg-compatible:1.0";
    private static readonly XNamespace Fo = "urn:oasis:names:tc:opendocument:xmlns:xsl-fo-compatible:1.0";
    private static readonly XNamespace Table = "urn:oasis:names:tc:opendocument:xmlns:table:1.0";
    private static readonly XNamespace Presentation = "urn:oasis:names:tc:opendocument:xmlns:presentation:1.0";

    private const double CmPerPt = 2.54 / 72;
    private const double DefaultColumnCm = 2.258;
    private const double DefaultRowCm = 0.452;

    public IReadOnlyCollection<string> Extensions { get; } = [".odt", ".ott", ".ods", ".ots", ".odp", ".otp"];

    public WatermarkResult Apply(string inputPath, string outputPath, WatermarkOptions options)
    {
        var pkg = ZipPackage.Load(inputPath);
        var mime = System.Text.Encoding.ASCII.GetString(pkg.Get("mimetype")).Trim();
        var warnings = new List<string>();

        var ctx = new ShapeContext(options);

        if (mime.StartsWith("application/vnd.oasis.opendocument.text", StringComparison.Ordinal))
            WatermarkText(pkg, ctx);
        else if (mime.StartsWith("application/vnd.oasis.opendocument.presentation", StringComparison.Ordinal))
            WatermarkPresentation(pkg, ctx);
        else if (mime.StartsWith("application/vnd.oasis.opendocument.spreadsheet", StringComparison.Ordinal))
            WatermarkSpreadsheet(pkg, ctx, warnings);
        else
            throw new NotSupportedException($"Unsupported OpenDocument type: {mime}");

        pkg.Save(outputPath);
        return new WatermarkResult(warnings);
    }

    // ---------------------------------------------------------------- testo

    private static void WatermarkText(ZipPackage pkg, ShapeContext ctx)
    {
        var styles = pkg.GetXml("styles.xml");
        var root = styles.Root!;
        var auto = GetOrAdd(root, Office + "automatic-styles", before: Office + "master-styles");
        ctx.AddStyles(auto);

        // Paragrafo minuscolo per le intestazioni create da noi.
        var tinyPara = $"{ctx.Prefix}H";
        auto.Add(new XElement(Style + "style",
            new XAttribute(Style + "name", tinyPara),
            new XAttribute(Style + "family", "paragraph"),
            new XElement(Style + "paragraph-properties",
                new XAttribute(Fo + "margin-top", "0cm"),
                new XAttribute(Fo + "margin-bottom", "0cm"),
                new XAttribute(Fo + "line-height", "100%")),
            new XElement(Style + "text-properties", new XAttribute(Fo + "font-size", "1pt"))));

        var masterStyles = root.Element(Office + "master-styles");
        if (masterStyles is null) return;

        foreach (var master in masterStyles.Elements(Style + "master-page"))
        {
            var layout = FindPageLayout(auto, (string?)master.Attribute(Style + "page-layout-name"));
            var props = layout?.Element(Style + "page-layout-properties");
            double pageW = Cm(props, Fo + "page-width", 21.0);
            double pageH = Cm(props, Fo + "page-height", 29.7);
            double marginL = Cm(props, Fo + "margin-left", 2.0);
            double marginT = Cm(props, Fo + "margin-top", 2.0);

            var header = master.Element(Style + "header");
            if (header is null || (string?)header.Attribute(Style + "display") == "false")
            {
                header?.Remove();
                header = new XElement(Style + "header",
                    new XElement(Text + "p", new XAttribute(Text + "style-name", tinyPara)));
                master.AddFirst(header);
                if (layout is not null) EnsureCompactHeaderStyle(layout);
            }

            // Anche le intestazioni alternative (pagine pari, prima pagina) se attive.
            var targets = new List<XElement> { header };
            targets.AddRange(master.Elements()
                .Where(e => e.Name == Style + "header-left" || e.Name == Style + "header-first")
                .Where(e => (string?)e.Attribute(Style + "display") != "false"));

            foreach (var h in targets)
            {
                var paragraph = h.Descendants().FirstOrDefault(e => e.Name == Text + "p" || e.Name == Text + "h");
                if (paragraph is null)
                {
                    paragraph = new XElement(Text + "p", new XAttribute(Text + "style-name", tinyPara));
                    h.Add(paragraph);
                }

                // Coordinate relative all'ancora (angolo in alto a sinistra dell'area dell'intestazione).
                var lineShapes = ctx.CreateShapes(pageW / 2 - marginL, pageH / 2 - marginT);
                foreach (var shape in lineShapes) shape.SetAttributeValue(Text + "anchor-type", "paragraph");
                paragraph.AddFirst(lineShapes);
            }
        }

        pkg.SetXml("styles.xml", styles);
    }

    private static void EnsureCompactHeaderStyle(XElement pageLayout)
    {
        var hs = pageLayout.Element(Style + "header-style");
        if (hs is null) { hs = new XElement(Style + "header-style"); pageLayout.Add(hs); }
        var hp = hs.Element(Style + "header-footer-properties");
        if (hp is null) { hp = new XElement(Style + "header-footer-properties"); hs.Add(hp); }
        hp.SetAttributeValue(Fo + "min-height", "0cm");
        hp.SetAttributeValue(Fo + "margin-bottom", "0cm");
    }

    // ---------------------------------------------------------------- presentazioni

    private static void WatermarkPresentation(ZipPackage pkg, ShapeContext ctx)
    {
        var styles = pkg.GetXml("styles.xml");
        var root = styles.Root!;
        var auto = GetOrAdd(root, Office + "automatic-styles", before: Office + "master-styles");
        ctx.AddStyles(auto);

        foreach (var master in root.Descendants(Style + "master-page"))
        {
            var props = FindPageLayout(auto, (string?)master.Attribute(Style + "page-layout-name"))
                ?.Element(Style + "page-layout-properties");
            double pageW = Cm(props, Fo + "page-width", 28.0);
            double pageH = Cm(props, Fo + "page-height", 15.75);

            var lineShapes = ctx.CreateShapes(pageW / 2, pageH / 2);
            foreach (var shape in lineShapes) shape.SetAttributeValue(Draw + "layer", "backgroundobjects");

            // Le note devono restare l'ultimo elemento della pagina master.
            var notes = master.Element(Presentation + "notes");
            if (notes is not null) notes.AddBeforeSelf(lineShapes);
            else master.Add(lineShapes);
        }

        pkg.SetXml("styles.xml", styles);
    }

    // ---------------------------------------------------------------- fogli di calcolo

    private static void WatermarkSpreadsheet(ZipPackage pkg, ShapeContext ctx, List<string> warnings)
    {
        var content = pkg.GetXml("content.xml");
        var root = content.Root!;
        var auto = GetOrAdd(root, Office + "automatic-styles", before: Office + "body");
        ctx.AddStyles(auto);

        var columnWidths = StyleLengths(auto, "table-column", "table-column-properties", Style + "column-width");
        var rowHeights = StyleLengths(auto, "table-row", "table-row-properties", Style + "row-height");

        double stepX = ctx.WidthCm + 4;
        double stepY = ctx.HeightCm + 8;

        foreach (var table in root.Descendants(Table + "table"))
        {
            // Area usata: si ignorano le ripetizioni enormi che LibreOffice scrive per le righe/colonne vuote finali.
            double usedW = table.Descendants(Table + "table-column")
                .Sum(c => Repeat(c, Table + "number-columns-repeated") * Lookup(columnWidths, c, DefaultColumnCm));
            double usedH = table.Descendants(Table + "table-row")
                .Sum(r => Repeat(r, Table + "number-rows-repeated") * Lookup(rowHeights, r, DefaultRowCm));

            usedW = Math.Clamp(usedW, 20, 300);
            usedH = Math.Clamp(usedH, 28, 600);

            var shapes = table.Element(Table + "shapes");
            if (shapes is null)
            {
                shapes = new XElement(Table + "shapes");
                var anchor = table.Elements().FirstOrDefault(e => e.Name.Namespace == Table && e.Name.LocalName is
                    "table-column" or "table-columns" or "table-column-group" or "table-header-columns" or
                    "table-row" or "table-rows" or "table-row-group" or "table-header-rows");
                if (anchor is not null) anchor.AddBeforeSelf(shapes);
                else table.Add(shapes);
            }

            int count = 0;
            for (double y = stepY / 2; y < usedH + stepY / 2 && count < 400; y += stepY)
                for (double x = stepX / 2; x < usedW + stepX / 2 && count < 400; x += stepX, count++)
                {
                    foreach (var shape in ctx.CreateShapes(x, y))
                    {
                        shape.SetAttributeValue(Table + "table-background", "true");
                        shapes.Add(shape);
                    }
                }

            if (count >= 400)
                warnings.Add($"Sheet '{(string?)table.Attribute(Table + "name")}': very large area, watermark limited to 400 repetitions.");
        }

        pkg.SetXml("content.xml", content);
    }

    private static Dictionary<string, double> StyleLengths(XElement auto, string family, string propsName, XName attr)
    {
        var map = new Dictionary<string, double>();
        foreach (var s in auto.Elements(Style + "style").Where(s => (string?)s.Attribute(Style + "family") == family))
        {
            var value = (string?)s.Element(Style + propsName)?.Attribute(attr);
            if (s.Attribute(Style + "name")?.Value is { } name && TryParseCm(value, out var cm))
                map[name] = cm;
        }
        return map;
    }

    private static double Lookup(Dictionary<string, double> map, XElement e, double fallback) =>
        e.Attribute(Table + "style-name")?.Value is { } n && map.TryGetValue(n, out var v) ? v : fallback;

    private static int Repeat(XElement e, XName attr)
    {
        int n = int.TryParse((string?)e.Attribute(attr), out var r) ? r : 1;
        return n > 1024 ? 0 : n;
    }

    // ---------------------------------------------------------------- forma Fontwork

    /// <summary>Crea le forme e i relativi stili, con nomi casuali per non essere facilmente individuabili.</summary>
    private sealed class ShapeContext
    {
        private readonly WatermarkOptions _o;
        private int _counter;

        public string Prefix { get; } = "W" + Convert.ToHexString(RandomNumberGenerator.GetBytes(3));
        private readonly string[] _lines;
        private readonly double[] _lineWidthsCm;
        private readonly double _lineHeightCm;

        /// <summary>Ingombro del blocco di righe (non ruotato), in centimetri.</summary>
        public double WidthCm { get; }
        public double HeightCm { get; }

        public ShapeContext(WatermarkOptions o)
        {
            _o = o;
            // Il testo Fontwork viene adattato alla forma: la larghezza è quella del testo misurato,
            // l'altezza quella delle maiuscole (circa il 75% della dimensione del carattere).
            // Ogni riga è una forma a sé: Fontwork allargherebbe le righe corte alla larghezza della più lunga.
            _lines = FontLoader.Lines(o.Text);
            _lineWidthsCm = _lines.Select(l => FontLoader.MeasureWidth(l, o.Font, o.FontSize) * CmPerPt).ToArray();
            _lineHeightCm = o.FontSize * 0.75 * CmPerPt;
            WidthCm = _lineWidthsCm.Max();
            HeightCm = _lineHeightCm + (_lines.Length - 1) * o.FontSize * FontLoader.LineSpacing * CmPerPt;
        }

        /// <summary>Una forma per riga; il blocco è centrato in (cx, cy) e ruota come un'unica scritta.</summary>
        public List<XElement> CreateShapes(double cx, double cy)
        {
            double a = _o.AngleDegrees * Math.PI / 180;
            var result = new List<XElement>();
            for (int i = 0; i < _lines.Length; i++)
            {
                double v = FontLoader.LineOffset(i, _lines.Length, _o.FontSize) * CmPerPt;
                result.Add(CreateShape(cx + v * Math.Sin(a), cy + v * Math.Cos(a), _lines[i], _lineWidthsCm[i]));
            }
            return result;
        }

        public void AddStyles(XElement automaticStyles)
        {
            automaticStyles.Add(new XElement(Style + "style",
                new XAttribute(Style + "name", Prefix + "G"),
                new XAttribute(Style + "family", "graphic"),
                new XElement(Style + "graphic-properties",
                    new XAttribute(Draw + "stroke", "none"),
                    new XAttribute(Draw + "fill", "solid"),
                    new XAttribute(Draw + "fill-color", "#" + _o.Color.RgbHex),
                    new XAttribute(Draw + "opacity", Percent(_o.Color.Opacity)),
                    new XAttribute(Draw + "shadow", "hidden"),
                    new XAttribute(Style + "run-through", "background"),
                    new XAttribute(Style + "wrap", "run-through"),
                    new XAttribute(Style + "protect", "position size"),
                    new XAttribute(Draw + "move-protect", "true"),
                    new XAttribute(Draw + "size-protect", "true"),
                    new XAttribute(Style + "print-content", "true"))));

            automaticStyles.Add(new XElement(Style + "style",
                new XAttribute(Style + "name", Prefix + "P"),
                new XAttribute(Style + "family", "paragraph"),
                new XElement(Style + "text-properties",
                    new XAttribute(Fo + "font-family", FontLoader.FamilyName(_o.Font)),
                    new XAttribute(Fo + "font-size", F(_o.FontSize) + "pt"),
                    new XAttribute(Fo + "font-weight", "bold"))));
        }

        /// <summary>Forma di una riga centrata nel punto (cx, cy), in centimetri rispetto all'ancora.</summary>
        private XElement CreateShape(double cx, double cy, string line, double w)
        {
            double a = _o.AngleDegrees * Math.PI / 180;
            double h = _lineHeightCm;

            // ODF ruota la forma attorno al suo angolo in alto a sinistra (Y verso il basso,
            // angolo positivo = antiorario): calcoliamo la traslazione che porta il centro in (cx, cy).
            double ox = w / 2 * Math.Cos(a) + h / 2 * Math.Sin(a);
            double oy = -w / 2 * Math.Sin(a) + h / 2 * Math.Cos(a);

            _counter++;
            return new XElement(Draw + "custom-shape",
                new XAttribute(Draw + "style-name", Prefix + "G"),
                new XAttribute(Draw + "name", $"Object {_counter}"),
                new XAttribute(Svg + "width", F(w) + "cm"),
                new XAttribute(Svg + "height", F(h) + "cm"),
                new XAttribute(Draw + "transform", $"rotate ({F(a, "0.######")}) translate ({F(cx - ox)}cm {F(cy - oy)}cm)"),
                new XElement(Text + "p", new XAttribute(Text + "style-name", Prefix + "P"), line),
                new XElement(Draw + "enhanced-geometry",
                    new XAttribute(Svg + "viewBox", "0 0 21600 21600"),
                    new XAttribute(Draw + "type", "fontwork-plain-text"),
                    new XAttribute(Draw + "text-path", "true"),
                    new XAttribute(Draw + "text-path-mode", "shape"),
                    new XAttribute(Draw + "text-path-scale", "all"),
                    new XAttribute(Draw + "text-path-same-letter-heights", "false"),
                    new XAttribute(Draw + "enhanced-path", "M 0 0 L 21600 0 N M 0 21600 L 21600 21600 N")));
        }

        private static string Percent(double opacity) =>
            Math.Round(opacity * 100).ToString(CultureInfo.InvariantCulture) + "%";
    }

    // ---------------------------------------------------------------- utilità

    private static XElement GetOrAdd(XElement parent, XName name, XName before)
    {
        var e = parent.Element(name);
        if (e is not null) return e;
        e = new XElement(name);
        var next = parent.Element(before);
        if (next is not null) next.AddBeforeSelf(e);
        else parent.Add(e);
        return e;
    }

    private static XElement? FindPageLayout(XElement automaticStyles, string? name) =>
        name is null ? null : automaticStyles.Elements(Style + "page-layout")
            .FirstOrDefault(l => (string?)l.Attribute(Style + "name") == name);

    private static double Cm(XElement? props, XName attr, double fallback) =>
        TryParseCm((string?)props?.Attribute(attr), out var v) && v > 0 ? v : fallback;

    private static readonly Regex LengthRegex = new(@"^\s*(-?[\d.]+)\s*(cm|mm|in|pt|pc|px)?\s*$", RegexOptions.Compiled);

    private static bool TryParseCm(string? value, out double cm)
    {
        cm = 0;
        if (value is null) return false;
        var m = LengthRegex.Match(value);
        if (!m.Success || !double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var n))
            return false;
        cm = m.Groups[2].Value switch
        {
            "mm" => n / 10,
            "in" => n * 2.54,
            "pt" => n * CmPerPt,
            "pc" => n * 12 * CmPerPt,
            "px" => n * 2.54 / 96,
            _ => n,
        };
        return true;
    }

    private static string F(double v, string format = "0.###") => v.ToString(format, CultureInfo.InvariantCulture);
}
