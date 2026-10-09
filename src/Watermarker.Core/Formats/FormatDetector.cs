using System.IO.Compression;
using System.Text;
using System.Xml.Linq;

namespace Watermarker.Core.Formats;

public enum FormatFamily
{
    Unknown,
    Pdf,
    Word,
    Excel,
    PowerPoint,
    Visio,
    Xps,
    OpenDocument,
    Image,
    Video,
    Zip,
    Email,
    /// <summary>Formati gestiti solo tramite conversione in PDF (doc, xls, ppt, rtf, txt, csv, dxf, svg...).</summary>
    ConvertibleOnly,
    /// <summary>Formati riconosciuti ma non gestiti (msg, xlsb...).</summary>
    Unsupported,
}

/// <summary>
/// Formato di un file, riconosciuto dal contenuto e non solo dall'estensione.
/// </summary>
/// <param name="Extension">Estensione con cui trattare il file (minuscola, con il punto).</param>
/// <param name="ExtensionMismatch">L'estensione del nome non corrisponde al contenuto.</param>
/// <param name="HasDigitalSignature">Il file contiene una firma digitale che la modifica invaliderà.</param>
public sealed record DetectedFormat(
    FormatFamily Family,
    string Extension,
    string Description,
    bool ExtensionMismatch = false,
    bool HasDigitalSignature = false);

/// <summary>
/// Riconosce il formato leggendo i primi byte e, per i pacchetti ZIP, la struttura interna.
/// Così un .docx rinominato in .pdf (o in .dat) viene comunque trattato come Word.
/// </summary>
public static class FormatDetector
{
    private static readonly string[] TextLikeConvertible = [".txt", ".csv", ".htm", ".html"];

    public static DetectedFormat Detect(string path, string? originalName = null)
    {
        var nameExt = Path.GetExtension(originalName ?? path).ToLowerInvariant();
        var head = ReadHead(path, 2048);

        var detected = DetectCore(path, head, nameExt);
        bool mismatch = detected.Family != FormatFamily.Unknown
                        && !string.IsNullOrEmpty(nameExt)
                        && !SameFamilyExtension(detected, nameExt);
        return detected with { ExtensionMismatch = mismatch };
    }

    private static DetectedFormat DetectCore(string path, byte[] head, string ext)
    {
        if (head.Length == 0) return new(FormatFamily.Unknown, ext, "Empty file");

        // PDF: l'intestazione può essere preceduta da qualche byte spurio.
        if (IndexOf(head, "%PDF-"u8) is >= 0 and < 1024)
            return new(FormatFamily.Pdf, ".pdf", "PDF");

        if (StartsWith(head, [0x50, 0x4B, 0x03, 0x04]) || StartsWith(head, [0x50, 0x4B, 0x05, 0x06]))
            return DetectZip(path, ext);

        if (StartsWith(head, [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1]))
            return DetectOle(ext);

        if (StartsWith(head, [0x89, 0x50, 0x4E, 0x47])) return Image(".png", "PNG");
        if (StartsWith(head, [0xFF, 0xD8, 0xFF])) return Image(ext is ".jpeg" or ".jpe" ? ext : ".jpg", "JPEG");
        if (StartsWith(head, "GIF8"u8)) return Image(".gif", "GIF");
        if (StartsWith(head, [0x49, 0x49, 0x2A, 0x00]) || StartsWith(head, [0x4D, 0x4D, 0x00, 0x2A]))
            return Image(ext == ".tiff" ? ".tiff" : ".tif", "TIFF");
        if (StartsWith(head, "BM"u8) && head.Length > 14 && ext == ".bmp") return Image(".bmp", "BMP");

        if (StartsWith(head, "RIFF"u8) && head.Length >= 12)
        {
            var kind = Encoding.ASCII.GetString(head, 8, 4);
            if (kind == "WEBP") return Image(".webp", "WebP");
            if (kind == "AVI ") return new(FormatFamily.Video, ".avi", "AVI video");
        }

        if (head.Length >= 12 && Encoding.ASCII.GetString(head, 4, 4) == "ftyp")
        {
            var brand = Encoding.ASCII.GetString(head, 8, 4);
            if (brand is "heic" or "heix" or "mif1" or "msf1" or "avif")
                return new(FormatFamily.Unsupported, ext, "HEIC/AVIF image");
            if (brand == "qt  ") return new(FormatFamily.Video, ".mov", "QuickTime video");
            return new(FormatFamily.Video, ext is ".m4v" or ".mov" ? ext : ".mp4", "MP4 video");
        }

        if (StartsWith(head, [0x1A, 0x45, 0xDF, 0xA3]))
            return new(FormatFamily.Video, ext == ".webm" ? ".webm" : ".mkv", "Matroska/WebM video");

        if (StartsWith(head, @"{\rtf"u8))
            return new(FormatFamily.ConvertibleOnly, ".rtf", "Rich Text Format");

        // Da qui in poi formati testuali: ci affidiamo all'estensione, verificando che non sia binario.
        if (LooksBinary(head))
            return new(FormatFamily.Unknown, ext, "Unrecognized format");

        return ext switch
        {
            ".eml" => new(FormatFamily.Email, ".eml", "Email (EML)"),
            ".svg" => new(FormatFamily.ConvertibleOnly, ".svg", "SVG vector image"),
            ".dxf" => new(FormatFamily.ConvertibleOnly, ".dxf", "DXF CAD drawing"),
            _ when TextLikeConvertible.Contains(ext) => new(FormatFamily.ConvertibleOnly, ext, "Text file"),
            _ => new(FormatFamily.Unknown, ext, "Unrecognized format"),
        };
    }

    // ------------------------------------------------------------------ ZIP / OPC / ODF

    private static DetectedFormat DetectZip(string path, string ext)
    {
        try
        {
            using var zip = ZipFile.OpenRead(path);
            var entries = zip.Entries.ToDictionary(e => e.FullName, StringComparer.OrdinalIgnoreCase);

            // OpenDocument: il primo file "mimetype" contiene il tipo.
            if (entries.TryGetValue("mimetype", out var mimeEntry))
            {
                var mime = ReadText(mimeEntry).Trim();
                bool signed = entries.Keys.Any(k => k.StartsWith("META-INF/documentsignatures", StringComparison.OrdinalIgnoreCase));
                var odf = DetectOdf(mime, ext);
                if (odf is not null) return odf with { HasDigitalSignature = signed };
            }

            if (entries.TryGetValue("[Content_Types].xml", out var ctEntry))
            {
                bool signed = entries.Keys.Any(k => k.StartsWith("_xmlsignatures/", StringComparison.OrdinalIgnoreCase)
                                                    || k.Contains("/_xmlsignatures/", StringComparison.OrdinalIgnoreCase)
                                                    || k.StartsWith("package/services/digital-signature", StringComparison.OrdinalIgnoreCase));
                var opc = DetectOpc(entries, ctEntry, ext);
                if (opc is not null) return opc with { HasDigitalSignature = signed };
            }

            return new(FormatFamily.Zip, ".zip", "ZIP archive");
        }
        catch (InvalidDataException)
        {
            return new(FormatFamily.Unknown, ext, "Damaged ZIP file");
        }
    }

    private static DetectedFormat? DetectOdf(string mime, string ext)
    {
        const string prefix = "application/vnd.oasis.opendocument.";
        if (!mime.StartsWith(prefix, StringComparison.Ordinal)) return null;

        return mime[prefix.Length..] switch
        {
            "text" => new(FormatFamily.OpenDocument, ".odt", "OpenDocument text"),
            "text-template" => new(FormatFamily.OpenDocument, ".ott", "OpenDocument text template"),
            "spreadsheet" => new(FormatFamily.OpenDocument, ".ods", "OpenDocument spreadsheet"),
            "spreadsheet-template" => new(FormatFamily.OpenDocument, ".ots", "OpenDocument spreadsheet template"),
            "presentation" => new(FormatFamily.OpenDocument, ".odp", "OpenDocument presentation"),
            "presentation-template" => new(FormatFamily.OpenDocument, ".otp", "OpenDocument presentation template"),
            "graphics" => new(FormatFamily.ConvertibleOnly, ".odg", "OpenDocument drawing"),
            _ => new(FormatFamily.Unsupported, ext, $"OpenDocument ({mime})"),
        };
    }

    private static DetectedFormat? DetectOpc(Dictionary<string, ZipArchiveEntry> entries, ZipArchiveEntry ctEntry, string ext)
    {
        // Parte principale: la relazione di primo livello in _rels/.rels.
        string? mainPart = null;
        bool isXps = false, isOxps = false;

        if (entries.TryGetValue("_rels/.rels", out var relsEntry))
        {
            var rels = XDocument.Parse(ReadText(relsEntry));
            foreach (var rel in rels.Root!.Elements())
            {
                var type = (string?)rel.Attribute("Type") ?? "";
                var target = (string?)rel.Attribute("Target") ?? "";
                if (type.EndsWith("/fixedrepresentation", StringComparison.OrdinalIgnoreCase))
                {
                    isXps = true;
                    isOxps = type.Contains("openxps", StringComparison.OrdinalIgnoreCase);
                }
                else if (type.EndsWith("/officeDocument", StringComparison.OrdinalIgnoreCase)
                         || type.EndsWith("/visio/2010/relationships/document", StringComparison.OrdinalIgnoreCase))
                {
                    mainPart ??= "/" + target.TrimStart('/');
                }
            }
        }

        if (isXps)
            return new(FormatFamily.Xps, isOxps || ext == ".oxps" ? ".oxps" : ".xps", "XPS document");
        if (mainPart is null) return null;

        var contentType = FindContentType(ReadText(ctEntry), mainPart).ToLowerInvariant();
        bool macro = contentType.Contains("macroenabled");
        bool template = contentType.Contains(".template");

        if (contentType.Contains("wordprocessingml") || contentType.Contains("ms-word"))
            return new(FormatFamily.Word, (template, macro) switch
            {
                (true, true) => ".dotm", (true, false) => ".dotx", (false, true) => ".docm", _ => ".docx",
            }, "Word document");

        if (contentType.Contains("spreadsheetml") || contentType.Contains("ms-excel"))
        {
            if (contentType.Contains("sheet.binary"))
                return new(FormatFamily.Unsupported, ".xlsb", "Excel binary workbook (.xlsb)");
            return new(FormatFamily.Excel, (template, macro) switch
            {
                (true, true) => ".xltm", (true, false) => ".xltx", (false, true) => ".xlsm", _ => ".xlsx",
            }, "Excel workbook");
        }

        if (contentType.Contains("presentationml") || contentType.Contains("ms-powerpoint"))
        {
            bool show = contentType.Contains(".slideshow");
            return new(FormatFamily.PowerPoint, (show, template, macro) switch
            {
                (true, _, true) => ".ppsm", (true, _, false) => ".ppsx",
                (_, true, true) => ".potm", (_, true, false) => ".potx",
                (_, _, true) => ".pptm", _ => ".pptx",
            }, "PowerPoint presentation");
        }

        if (contentType.Contains("ms-visio"))
        {
            if (contentType.Contains(".stencil"))
                return new(FormatFamily.Unsupported, ".vssx", "Visio stencil");
            return new(FormatFamily.Visio, (template, macro) switch
            {
                (true, true) => ".vstm", (true, false) => ".vstx", (false, true) => ".vsdm", _ => ".vsdx",
            }, "Visio drawing");
        }

        return new(FormatFamily.Unsupported, ext, "Unrecognized Office package");
    }

    private static string FindContentType(string contentTypesXml, string partName)
    {
        var doc = XDocument.Parse(contentTypesXml);
        foreach (var o in doc.Root!.Elements().Where(e => e.Name.LocalName == "Override"))
            if (string.Equals((string?)o.Attribute("PartName"), partName, StringComparison.OrdinalIgnoreCase))
                return (string?)o.Attribute("ContentType") ?? "";

        var partExt = Path.GetExtension(partName).TrimStart('.');
        foreach (var d in doc.Root!.Elements().Where(e => e.Name.LocalName == "Default"))
            if (string.Equals((string?)d.Attribute("Extension"), partExt, StringComparison.OrdinalIgnoreCase))
                return (string?)d.Attribute("ContentType") ?? "";

        return "";
    }

    // ------------------------------------------------------------------ OLE2 (Office 97-2003)

    private static DetectedFormat DetectOle(string ext) => ext switch
    {
        ".doc" or ".dot" => new(FormatFamily.ConvertibleOnly, ext, "Word 97-2003 document"),
        ".xls" or ".xlt" => new(FormatFamily.ConvertibleOnly, ext, "Excel 97-2003 workbook"),
        ".ppt" or ".pot" or ".pps" => new(FormatFamily.ConvertibleOnly, ext, "PowerPoint 97-2003 presentation"),
        ".vsd" or ".vst" => new(FormatFamily.ConvertibleOnly, ext, "Visio 2003-2010 drawing"),
        ".msg" => new(FormatFamily.Unsupported, ".msg", "Outlook message (.msg)"),
        _ => new(FormatFamily.Unsupported, ext, "Unrecognized Office 97-2003 file"),
    };

    // ------------------------------------------------------------------ utilità

    /// <summary>Estensioni considerate equivalenti per lo stesso contenuto (es. .jpg/.jpeg).</summary>
    private static bool SameFamilyExtension(DetectedFormat f, string nameExt)
    {
        if (f.Extension == nameExt) return true;
        string[][] groups =
        [
            [".jpg", ".jpeg", ".jpe"], [".tif", ".tiff"], [".mp4", ".m4v"], [".mkv", ".webm"],
            [".htm", ".html"], [".xps", ".oxps"],
        ];
        return groups.Any(g => g.Contains(f.Extension) && g.Contains(nameExt));
    }

    private static DetectedFormat Image(string ext, string name) => new(FormatFamily.Image, ext, $"{name} image");

    private static byte[] ReadHead(string path, int count)
    {
        using var fs = File.OpenRead(path);
        var buffer = new byte[Math.Min(count, fs.Length)];
        fs.ReadExactly(buffer);
        return buffer;
    }

    private static bool StartsWith(byte[] data, ReadOnlySpan<byte> prefix) => data.AsSpan().StartsWith(prefix);

    private static int IndexOf(byte[] data, ReadOnlySpan<byte> value) => data.AsSpan().IndexOf(value);

    /// <summary>Un file di testo non contiene byte nulli (UTF-16 escluso, raro per eml/csv/txt).</summary>
    private static bool LooksBinary(byte[] head)
    {
        if (head.Length >= 2 && ((head[0] == 0xFF && head[1] == 0xFE) || (head[0] == 0xFE && head[1] == 0xFF)))
            return false; // UTF-16 con BOM
        return head.AsSpan().IndexOf((byte)0) >= 0;
    }

    private static string ReadText(ZipArchiveEntry entry)
    {
        using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
