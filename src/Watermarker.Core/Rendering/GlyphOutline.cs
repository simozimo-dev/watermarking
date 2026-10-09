using System.Globalization;
using System.Text;
using SkiaSharp;

namespace Watermarker.Core.Rendering;

internal readonly record struct PointD(double X, double Y);

internal enum PathVerb { MoveTo, LineTo, CurveTo, Close }

internal readonly record struct PathSegment(PathVerb Verb, PointD P1 = default, PointD P2 = default, PointD P3 = default);

/// <summary>
/// Il testo convertito in contorni vettoriali (curve di Bézier), centrato nell'origine e con l'asse Y verso l'alto
/// (convenzione PDF). Disegnare i contorni al posto del testo significa che il watermark:
///  - non compare nella selezione, nel copia/incolla né nella ricerca del documento;
///  - non è modificabile come testo e non dipende da un font incorporato.
/// </summary>
internal sealed class GlyphOutline
{
    public required IReadOnlyList<PathSegment> Segments { get; init; }
    public double Width { get; init; }
    public double Height { get; init; }

    public static GlyphOutline Create(string text, string font, double fontSize)
    {
        using var skFont = new SKFont(FontLoader.Load(font), (float)fontSize);
        using var path = new SKPath();

        // Una riga sotto l'altra, ciascuna centrata orizzontalmente.
        var lines = FontLoader.Lines(text);
        for (int i = 0; i < lines.Length; i++)
        {
            float width = skFont.MeasureText(lines[i]);
            float y = (float)FontLoader.LineOffset(i, lines.Length, fontSize);
            using var linePath = skFont.GetTextPath(lines[i], new SKPoint(-width / 2, y));
            path.AddPath(linePath);
        }

        var bounds = path.TightBounds;
        if (bounds.Width <= 0 || bounds.Height <= 0)
            throw new InvalidOperationException(
                $"The font '{font}' has no glyphs for this text. Try a different font.");

        float cx = bounds.MidX, cy = bounds.MidY;
        // Da coordinate Skia (Y verso il basso) a coordinate PDF (Y verso l'alto), centrate.
        PointD T(SKPoint p) => new(p.X - cx, -(p.Y - cy));

        var segments = new List<PathSegment>();
        var pts = new SKPoint[4];
        using var it = path.CreateRawIterator();
        SKPathVerb verb;

        while ((verb = it.Next(pts)) != SKPathVerb.Done)
        {
            switch (verb)
            {
                case SKPathVerb.Move:
                    segments.Add(new(PathVerb.MoveTo, T(pts[0])));
                    break;
                case SKPathVerb.Line:
                    segments.Add(new(PathVerb.LineTo, T(pts[1])));
                    break;
                case SKPathVerb.Quad:
                    segments.Add(QuadToCubic(pts[0], pts[1], pts[2], T));
                    break;
                case SKPathVerb.Conic:
                    var quads = SKPath.ConvertConicToQuads(pts[0], pts[1], pts[2], it.ConicWeight(), 2);
                    for (int i = 1; i + 1 < quads.Length; i += 2)
                        segments.Add(QuadToCubic(quads[i - 1], quads[i], quads[i + 1], T));
                    break;
                case SKPathVerb.Cubic:
                    segments.Add(new(PathVerb.CurveTo, T(pts[1]), T(pts[2]), T(pts[3])));
                    break;
                case SKPathVerb.Close:
                    segments.Add(new(PathVerb.Close));
                    break;
            }
        }

        return new GlyphOutline { Segments = segments, Width = bounds.Width, Height = bounds.Height };
    }

    private static PathSegment QuadToCubic(SKPoint p0, SKPoint q, SKPoint p2, Func<SKPoint, PointD> t)
    {
        var c1 = new SKPoint(p0.X + 2f / 3f * (q.X - p0.X), p0.Y + 2f / 3f * (q.Y - p0.Y));
        var c2 = new SKPoint(p2.X + 2f / 3f * (q.X - p2.X), p2.Y + 2f / 3f * (q.Y - p2.Y));
        return new(PathVerb.CurveTo, t(c1), t(c2), t(p2));
    }

    /// <summary>Operatori PDF del percorso (m, l, c, h), senza l'operatore di riempimento.</summary>
    public string ToPdfOperators()
    {
        var sb = new StringBuilder(Segments.Count * 24);
        foreach (var s in Segments)
        {
            switch (s.Verb)
            {
                case PathVerb.MoveTo: sb.Append(F(s.P1.X)).Append(' ').Append(F(s.P1.Y)).Append(" m\n"); break;
                case PathVerb.LineTo: sb.Append(F(s.P1.X)).Append(' ').Append(F(s.P1.Y)).Append(" l\n"); break;
                case PathVerb.CurveTo:
                    sb.Append(F(s.P1.X)).Append(' ').Append(F(s.P1.Y)).Append(' ')
                      .Append(F(s.P2.X)).Append(' ').Append(F(s.P2.Y)).Append(' ')
                      .Append(F(s.P3.X)).Append(' ').Append(F(s.P3.Y)).Append(" c\n");
                    break;
                case PathVerb.Close: sb.Append("h\n"); break;
            }
        }
        return sb.ToString();
    }

    private static string F(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);
}
