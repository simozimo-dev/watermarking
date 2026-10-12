using System.Globalization;
using Microsoft.AspNetCore.StaticFiles;
using Watermarker.Core;

namespace Watermarker.Web;

/// <summary>Chi compare in {user} ed {email}.</summary>
public sealed record WatermarkIdentity(string? Name, string? Email);

/// <summary>Esito di un'elaborazione riuscita.</summary>
public sealed record ProcessingOutcome(
    string OutputPath,
    string DownloadName,
    string ContentType,
    string Format,
    IReadOnlyList<string> Warnings);

/// <summary>
/// Parte comune a portale, API sincrona e lavori asincroni: lettura delle impostazioni dal form,
/// elaborazione e traduzione degli errori in risposte HTTP.
/// </summary>
public static class Processing
{
    private static readonly FileExtensionContentTypeProvider ContentTypes = new();

    /// <summary>
    /// Legge le impostazioni del watermark dai campi del form (stessi nomi per pagina e API).
    /// <paramref name="fixedText"/>, se presente, sostituisce il testo inviato dal chiamante.
    /// </summary>
    public static bool TryReadOptions(IFormCollection form, PortalOptions portal, string fileName,
                                      WatermarkIdentity identity, string? fixedText,
                                      out WatermarkOptions options, out OutputMode mode, out string error)
    {
        options = null!;
        mode = OutputMode.Original;
        error = "";

        // Testo su più righe: a capo normalizzati, righe vuote ignorate.
        var raw = string.IsNullOrWhiteSpace(fixedText) ? form["text"].ToString() : fixedText;
        var text = raw.Replace("\r\n", "\n").Replace('\r', '\n').Trim();
        if (text.Length == 0) { error = "Enter the watermark text."; return false; }
        if (text.Length > portal.MaxTextLength) { error = $"The text can be at most {portal.MaxTextLength} characters long."; return false; }

        if (text.Split('\n').Count(l => l.Trim().Length > 0) > WatermarkOptions.MaxLines)
        { error = $"The watermark can have at most {WatermarkOptions.MaxLines} lines."; return false; }

        if (!TryNumber(form["angle"], -360, 360, 45, out var angle)) { error = "Invalid angle (-360 to 360)."; return false; }
        if (!TryNumber(form["size"], 4, 400, 60, out var size)) { error = "Invalid size (4-400 points)."; return false; }
        if (!TryNumber(form["opacity"], 0.05, 1, 0.35, out var opacity)) { error = "Invalid opacity (0.05-1)."; return false; }

        RgbaColor color;
        try { color = RgbaColor.Parse(form["color"].ToString() is { Length: > 0 } c ? c : "#808080").WithOpacity(opacity); }
        catch (FormatException ex) { error = ex.Message; return false; }

        var font = form["font"].ToString();
        var fonts = portal.EffectiveFonts;
        if (string.IsNullOrEmpty(font)) font = fonts[0];
        var match = fonts.FirstOrDefault(f => string.Equals(f, font, StringComparison.OrdinalIgnoreCase));
        if (match is null) { error = $"Font not available. Choose one of: {string.Join(", ", fonts)}."; return false; }

        var output = form["output"].ToString();
        if (output.Length > 0 && !output.Equals("original", StringComparison.OrdinalIgnoreCase)
                              && !output.Equals("pdf", StringComparison.OrdinalIgnoreCase))
        { error = "Invalid output: use \"original\" or \"pdf\"."; return false; }
        mode = output.Equals("pdf", StringComparison.OrdinalIgnoreCase) ? OutputMode.Pdf : OutputMode.Original;

        var expanded = TextTemplate.Expand(text, fileName, identity.Name, email: identity.Email);
        if (expanded.Length == 0) { error = "The watermark text is empty after replacing the placeholders."; return false; }

        options = new WatermarkOptions
        {
            Text = expanded,
            AngleDegrees = angle,
            FontSize = size,
            Color = color,
            Font = match,
            Tile = IsOn(form["tile"]),
            PowerPointOnSlides = IsOn(form["pptxSlides"]),
        };
        return true;
    }

    /// <summary>
    /// Riconosce il formato e applica il watermark. Il file prodotto è
    /// <paramref name="outputBase"/> più l'estensione del formato di uscita.
    /// </summary>
    public static async Task<ProcessingOutcome> RunAsync(WatermarkEngine engine, string inputPath, string originalName,
                                                         string outputBase, WatermarkOptions options, OutputMode mode,
                                                         CancellationToken ct)
    {
        var format = engine.Detect(inputPath, originalName);
        var downloadName = WatermarkEngine.SuggestOutputName(originalName, format, mode);
        var output = outputBase + Path.GetExtension(downloadName);

        var result = await Task.Run(() => engine.Process(inputPath, output, options, mode, originalName), ct);
        return new ProcessingOutcome(output, downloadName, ContentType(downloadName), format.Description, result.Warnings);
    }

    /// <summary>Stato HTTP, titolo e messaggio per l'utente corrispondenti a un errore di elaborazione.</summary>
    public static (int Status, string Title, string Detail) Classify(Exception ex) => ex switch
    {
        NotSupportedException => (415, "Unsupported format", ex.Message),
        InvalidDataException or FormatException or ArgumentException => (422, "File could not be processed", ex.Message),
        TimeoutException => (504, "Processing took too long", ex.Message),
        _ => (500, "Processing failed", "The file may be damaged or password-protected. Details are in the server log."),
    };

    public static void LogFailure(ILogger log, Exception ex, int status, string fileName)
    {
        if (status >= 500 && status != 504) log.LogError(ex, "Error processing {File}", fileName);
        else log.LogWarning(ex, "File could not be processed: {File} ({Status})", fileName, status);
    }

    public static string ContentType(string fileName) =>
        ContentTypes.TryGetContentType(fileName, out var ct) ? ct : "application/octet-stream";

    /// <summary>Apre il risultato in lettura: il file viene cancellato appena il download è terminato.</summary>
    public static FileStream OpenAndDeleteOnClose(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete,
            81920, FileOptions.DeleteOnClose | FileOptions.Asynchronous);

    public static IResult Problem(int status, string title, string detail) =>
        Results.Problem(statusCode: status, title: title, detail: detail);

    private static bool TryNumber(string? value, double min, double max, double fallback, out double result)
    {
        if (string.IsNullOrWhiteSpace(value)) { result = fallback; return true; }
        return double.TryParse(value.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out result)
               && result >= min && result <= max;
    }

    private static bool IsOn(string? value) =>
        value is not null && (value == "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase)
                                           || value.Equals("on", StringComparison.OrdinalIgnoreCase));
}
