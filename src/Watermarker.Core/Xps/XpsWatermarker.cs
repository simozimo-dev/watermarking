using System.Globalization;
using System.Text;
using System.Xml.Linq;
using Watermarker.Core.Packaging;
using Watermarker.Core.Rendering;

namespace Watermarker.Core.Xps;

/// <summary>
/// Watermark per XPS e OpenXPS: come nel PDF il testo viene convertito in contorni vettoriali
/// (elemento Path) e aggiunto in primo piano su ogni pagina (FixedPage).
/// Il watermark non è testo: selezione, copia e ricerca del documento restano identiche.
/// </summary>
public sealed class XpsWatermarker : IWatermarker
{
    private const double XpsUnitsPerPt = 96.0 / 72.0;

    public IReadOnlyCollection<string> Extensions { get; } = [".xps", ".oxps"];

    public WatermarkResult Apply(string inputPath, string outputPath, WatermarkOptions options)
    {
        var pkg = ZipPackage.Load(inputPath);
        var warnings = new List<string>();

        if (pkg.Names.Any(n => n.Contains(".fpage/", StringComparison.OrdinalIgnoreCase)))
            throw new NotSupportedException("XPS files with interleaved page pieces are not supported.");

        var outline = GlyphOutline.Create(options.Text, options.Font, options.FontSize);
        var data = ToXpsGeometry(outline);

        int pages = 0;
        foreach (var name in pkg.Names.Where(n => n.EndsWith(".fpage", StringComparison.OrdinalIgnoreCase)).ToList())
        {
            var doc = pkg.GetXml(name);
            var page = doc.Root!;
            XNamespace ns = page.Name.Namespace;

            double w = Number(page.Attribute("Width")) ?? 816;  // 8.5" a 96 dpi
            double h = Number(page.Attribute("Height")) ?? 1056;

            var canvas = new XElement(ns + "Canvas");
            foreach (var (x, y) in Positions(options, outline, w, h))
                canvas.Add(CreatePath(ns, data, options, x, y));

            page.Add(canvas); // ultimo figlio = in primo piano
            pkg.SetXml(name, doc);
            pages++;
        }

        if (pages == 0) throw new InvalidDataException("No pages found in the XPS document.");
        if (pkg.Names.Any(n => n.Contains("signature", StringComparison.OrdinalIgnoreCase) && n.EndsWith(".psdsxs", StringComparison.OrdinalIgnoreCase)))
            warnings.Add("The document was digitally signed: the signature is no longer valid.");

        pkg.Save(outputPath);
        return new WatermarkResult(warnings);
    }

    private static XElement CreatePath(XNamespace ns, string data, WatermarkOptions o, double cx, double cy)
    {
        // Rotazione antioraria a schermo (asse Y verso il basso) e traslazione al punto (cx, cy).
        double a = o.AngleDegrees * Math.PI / 180;
        double cos = Math.Cos(a), sin = Math.Sin(a);
        string matrix = string.Join(",", new[] { cos, -sin, sin, cos, cx, cy }.Select(v => F(v)));

        return new XElement(ns + "Path",
            new XAttribute("Data", data),
            new XAttribute("Fill", "#FF" + o.Color.RgbHex),
            new XAttribute("Opacity", F(o.Color.Opacity)),
            new XAttribute("RenderTransform", matrix));
    }

    private static IEnumerable<(double X, double Y)> Positions(WatermarkOptions o, GlyphOutline outline, double w, double h)
    {
        double cx = w / 2, cy = h / 2;
        if (!o.Tile)
        {
            yield return (cx, cy);
            yield break;
        }

        // Griglia nel sistema ruotato del testo, riportata sulla pagina.
        double a = o.AngleDegrees * Math.PI / 180;
        double cos = Math.Cos(a), sin = Math.Sin(a);
        double stepU = (outline.Width + o.FontSize * 1.5) * XpsUnitsPerPt;
        double stepV = (outline.Height + o.FontSize * 3) * XpsUnitsPerPt;
        double radius = Math.Sqrt(w * w + h * h) / 2 + Math.Max(stepU, stepV);

        int row = 0;
        for (double v = -radius; v <= radius; v += stepV, row++)
        {
            double offset = row % 2 == 0 ? 0 : stepU / 2;
            for (double u = -radius + offset; u <= radius; u += stepU)
                yield return (cx + u * cos + v * sin, cy - u * sin + v * cos);
        }
    }

    /// <summary>Geometria in sintassi abbreviata XPS, in unità XPS (1/96") con Y verso il basso.</summary>
    private static string ToXpsGeometry(GlyphOutline outline)
    {
        var sb = new StringBuilder("F1");
        string P(PointD p) => F(p.X * XpsUnitsPerPt) + "," + F(-p.Y * XpsUnitsPerPt);

        foreach (var s in outline.Segments)
        {
            switch (s.Verb)
            {
                case PathVerb.MoveTo: sb.Append(" M").Append(P(s.P1)); break;
                case PathVerb.LineTo: sb.Append(" L").Append(P(s.P1)); break;
                case PathVerb.CurveTo: sb.Append(" C").Append(P(s.P1)).Append(' ').Append(P(s.P2)).Append(' ').Append(P(s.P3)); break;
                case PathVerb.Close: sb.Append(" Z"); break;
            }
        }
        return sb.ToString();
    }

    private static double? Number(XAttribute? a) =>
        double.TryParse(a?.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;

    private static string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
}
