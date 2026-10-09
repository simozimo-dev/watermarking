using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.IO;
using Watermarker.Core.Rendering;

namespace Watermarker.Core.Pdf;

/// <summary>
/// Watermark PDF progettato per essere difficile da rimuovere senza toccare le funzionalità:
///
///  1. Il testo viene disegnato come contorni vettoriali (non come testo): selezione, copia,
///     ricerca e accessibilità del testo originale restano identiche, e il watermark non si
///     modifica con gli strumenti "modifica testo".
///  2. Il watermark viene fuso con il contenuto originale della pagina in un unico content
///     stream compresso: non esiste un oggetto separato (annotazione, livello OCG, XObject,
///     stream aggiuntivo) da eliminare con un click o con uno script banale.
///  3. Non usa la marcatura che Acrobat riconosce come "watermark" (/Artifact con Subtype
///     /Watermark, /PieceInfo ADBE_CompoundType), quindi "Rimuovi filigrana" non lo trova.
///     È marcato solo come /Artifact generico, così gli screen reader lo ignorano.
///  4. Gli stream originali non sono più referenziati e PDFsharp li elimina al salvataggio,
///     quindi non si può "ricollegare" la versione senza watermark.
///
/// Limite onesto: chi ha competenze sul formato PDF può comunque analizzare il content
/// stream e togliere i percorsi. L'obiettivo è renderlo laborioso, non impossibile.
/// </summary>
public sealed class PdfWatermarker : IWatermarker
{
    private static readonly HashSet<string> DecodableFilters =
        ["/FlateDecode", "/Fl", "/LZWDecode", "/LZW", "/ASCIIHexDecode", "/AHx", "/ASCII85Decode", "/A85"];

    public IReadOnlyCollection<string> Extensions { get; } = [".pdf"];

    public WatermarkResult Apply(string inputPath, string outputPath, WatermarkOptions options)
    {
        var warnings = new List<string>();
        var outline = GlyphOutline.Create(options.Text, options.Font, options.FontSize);
        var pathOps = outline.ToPdfOperators();
        // Nome della risorsa di opacità generato a caso: nessun nome "parlante" da cercare.
        var gsName = "/GS" + Convert.ToHexString(RandomNumberGenerator.GetBytes(3));

        using var document = PdfReader.Open(inputPath, PdfDocumentOpenMode.Modify);

        int pageNumber = 0;
        foreach (PdfPage page in document.Pages)
        {
            pageNumber++;
            RegisterOpacityState(document, page, gsName, options.Color.Opacity);
            var watermarkOps = BuildWatermarkOperators(page, outline, pathOps, gsName, options);

            if (!MergeIntoSingleStream(document, page, watermarkOps))
            {
                warnings.Add($"Page {pageNumber}: content stream uses an unsupported filter, " +
                             "watermark added as a separate stream (less robust).");
            }
        }

        document.Save(outputPath);
        return new WatermarkResult(warnings);
    }

    // ---------------------------------------------------------------- contenuto

    private static string BuildWatermarkOperators(
        PdfPage page, GlyphOutline outline, string pathOps, string gsName, WatermarkOptions o)
    {
        var (x1, y1, x2, y2) = GetVisibleBox(page);
        double cx = (x1 + x2) / 2, cy = (y1 + y2) / 2;

        // Le pagine con /Rotate vengono mostrate ruotate in senso orario: compensiamo,
        // così l'inclinazione percepita è sempre quella richiesta.
        double rad = (o.AngleDegrees + page.Rotate) * Math.PI / 180.0;
        double cos = Math.Cos(rad), sin = Math.Sin(rad);

        var sb = new StringBuilder();
        sb.Append("/Artifact BMC\nq\n");
        sb.Append(gsName).Append(" gs\n");
        sb.Append(F(o.Color.R / 255.0)).Append(' ')
          .Append(F(o.Color.G / 255.0)).Append(' ')
          .Append(F(o.Color.B / 255.0)).Append(" rg\n");

        foreach (var (u, v) in Positions(outline, o, x2 - x1, y2 - y1))
        {
            // (u, v) sono coordinate nel sistema ruotato del testo: le riportiamo sulla pagina.
            double px = cx + u * cos - v * sin;
            double py = cy + u * sin + v * cos;
            sb.Append("q ")
              .Append(F(cos)).Append(' ').Append(F(sin)).Append(' ')
              .Append(F(-sin)).Append(' ').Append(F(cos)).Append(' ')
              .Append(F(px)).Append(' ').Append(F(py)).Append(" cm\n")
              .Append(pathOps)
              .Append("f\nQ\n");
        }

        sb.Append("Q\nEMC\n");
        return sb.ToString();
    }

    private static IEnumerable<(double U, double V)> Positions(
        GlyphOutline outline, WatermarkOptions o, double pageW, double pageH)
    {
        if (!o.Tile)
        {
            yield return (0, 0);
            yield break;
        }

        double stepU = outline.Width + o.FontSize * 1.5;
        double stepV = outline.Height + o.FontSize * 3;
        double radius = Math.Sqrt(pageW * pageW + pageH * pageH) / 2 + Math.Max(stepU, stepV) / 2;

        int row = 0;
        for (double v = -radius; v <= radius; v += stepV, row++)
        {
            double offset = row % 2 == 0 ? 0 : stepU / 2; // righe sfalsate
            for (double u = -radius + offset; u <= radius; u += stepU)
                yield return (u, v);
        }
    }

    /// <summary>
    /// Sostituisce /Contents con un unico stream: q [contenuto originale] Q [watermark].
    /// Restituisce false se ha dovuto ripiegare sull'aggiunta di uno stream separato.
    /// </summary>
    private static bool MergeIntoSingleStream(PdfDocument doc, PdfPage page, string watermarkOps)
    {
        var originals = EnumerateContentStreams(page.Elements["/Contents"]).ToList();
        var wmBytes = Encoding.ASCII.GetBytes(watermarkOps);

        if (originals.Any(s => !CanDecode(s)))
        {
            // Fallback: non possiamo decodificare il contenuto in sicurezza, quindi
            // aggiungiamo q/Q e il watermark come stream separati senza toccare l'originale.
            var array = new PdfArray(doc);
            array.Elements.Add(NewStream(doc, "q\n"u8.ToArray()).Reference!);
            foreach (var s in originals) array.Elements.Add(s.Reference is { } r ? r : (PdfItem)s);
            var tail = new byte[3 + wmBytes.Length];
            "\nQ\n"u8.CopyTo(tail);
            wmBytes.CopyTo(tail, 3);
            array.Elements.Add(NewStream(doc, tail).Reference!);
            page.Elements["/Contents"] = array;
            return false;
        }

        using var ms = new MemoryStream();
        ms.Write("q\n"u8);
        foreach (var s in originals)
        {
            var data = s.Stream?.UnfilteredValue;
            if (data is { Length: > 0 })
            {
                ms.Write(data);
                ms.WriteByte((byte)'\n');
            }
        }
        ms.Write("Q\n"u8);
        ms.Write(wmBytes);

        var merged = NewStream(doc, ms.ToArray());
        page.Elements.SetReference("/Contents", merged);
        return true;
    }

    private static PdfDictionary NewStream(PdfDocument doc, byte[] unfiltered)
    {
        var dict = new PdfDictionary(doc);
        dict.CreateStream(Deflate(unfiltered));
        dict.Elements["/Filter"] = new PdfName("/FlateDecode");
        doc.Internals.AddObject(dict);
        return dict;
    }

    private static IEnumerable<PdfDictionary> EnumerateContentStreams(PdfItem? item)
    {
        switch (item)
        {
            case PdfReference r:
                foreach (var d in EnumerateContentStreams(r.Value)) yield return d;
                break;
            case PdfArray a:
                foreach (var e in a.Elements)
                    foreach (var d in EnumerateContentStreams(e)) yield return d;
                break;
            case PdfDictionary d:
                yield return d;
                break;
        }
    }

    private static bool CanDecode(PdfDictionary stream)
    {
        var raw = stream.Elements["/Filter"];
        var filter = raw is PdfReference r ? r.Value : raw;
        return filter switch
        {
            null => true,
            PdfName n => DecodableFilters.Contains(n.Value),
            PdfArray arr => arr.Elements.All(e => e is PdfName n && DecodableFilters.Contains(n.Value)),
            _ => false,
        };
    }

    // ---------------------------------------------------------------- risorse

    /// <summary>Registra uno stato grafico con l'opacità richiesta nelle risorse della pagina.</summary>
    private static void RegisterOpacityState(PdfDocument doc, PdfPage page, string gsName, double opacity)
    {
        var resources = EnsurePageResources(doc, page);

        if (Resolve(resources.Elements["/ExtGState"]) is not PdfDictionary extGStates)
        {
            extGStates = new PdfDictionary(doc);
            resources.Elements["/ExtGState"] = extGStates;
        }

        if (extGStates.Elements.ContainsKey(gsName)) return; // risorse condivise tra pagine

        var gs = new PdfDictionary(doc);
        gs.Elements["/Type"] = new PdfName("/ExtGState");
        gs.Elements["/ca"] = new PdfReal(opacity); // opacità del riempimento
        gs.Elements["/CA"] = new PdfReal(opacity); // opacità del tratto
        extGStates.Elements[gsName] = gs;
    }

    /// <summary>Restituisce il dizionario /Resources della pagina, copiando quello ereditato se serve.</summary>
    private static PdfDictionary EnsurePageResources(PdfDocument doc, PdfPage page)
    {
        if (Resolve(page.Elements["/Resources"]) is PdfDictionary own) return own;

        PdfDictionary? inherited = null;
        var node = Resolve(page.Elements["/Parent"]);
        while (node is not null && inherited is null)
        {
            inherited = Resolve(node.Elements["/Resources"]);
            node = Resolve(node.Elements["/Parent"]);
        }

        var res = new PdfDictionary(doc);
        if (inherited is not null)
            foreach (var key in inherited.Elements.Keys.ToList())
                res.Elements[key] = inherited.Elements[key];

        page.Elements["/Resources"] = res;
        return res;
    }

    private static (double X1, double Y1, double X2, double Y2) GetVisibleBox(PdfPage page)
    {
        foreach (var key in new[] { "/CropBox", "/MediaBox" })
        {
            var r = page.Elements.GetRectangle(key);
            if (Math.Abs(r.X2 - r.X1) > 0 && Math.Abs(r.Y2 - r.Y1) > 0)
                return (Math.Min(r.X1, r.X2), Math.Min(r.Y1, r.Y2), Math.Max(r.X1, r.X2), Math.Max(r.Y1, r.Y2));
        }
        return (0, 0, 595, 842); // A4 come ultima risorsa
    }

    // ---------------------------------------------------------------- utilità

    private static PdfDictionary? Resolve(PdfItem? item) => item switch
    {
        PdfReference r => r.Value as PdfDictionary,
        PdfDictionary d => d,
        _ => null,
    };

    private static byte[] Deflate(byte[] data)
    {
        using var output = new MemoryStream();
        using (var z = new ZLibStream(output, CompressionLevel.Optimal, leaveOpen: true))
            z.Write(data);
        return output.ToArray();
    }

    private static string F(double v) => v.ToString("0.####", CultureInfo.InvariantCulture);
}
