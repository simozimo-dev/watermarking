using System.Collections.Concurrent;

namespace Watermarker.Core.Conversion;

/// <summary>
/// Conversione in PDF con LibreOffice in modalità headless.
///
/// Ogni conversione usa un profilo utente dedicato: LibreOffice non permette a due processi
/// di condividere lo stesso profilo, quindi senza questo accorgimento le conversioni
/// parallele fallirebbero. I profili vengono riutilizzati (crearli richiede alcuni secondi)
/// e il numero di conversioni contemporanee è limitato.
/// </summary>
public sealed class LibreOfficeConverter : IPdfConverter
{
    /// <summary>Estensioni che LibreOffice sa convertire in PDF.</summary>
    public static IReadOnlyCollection<string> SupportedExtensions { get; } =
    [
        // Word e testo
        ".doc", ".dot", ".docx", ".docm", ".dotx", ".dotm", ".rtf", ".odt", ".ott", ".txt", ".htm", ".html",
        // Excel e dati
        ".xls", ".xlt", ".xlsx", ".xlsm", ".xltx", ".xltm", ".ods", ".ots", ".csv",
        // PowerPoint
        ".ppt", ".pot", ".pps", ".pptx", ".pptm", ".potx", ".potm", ".ppsx", ".ppsm", ".odp", ".otp",
        // Disegni
        ".vsd", ".vst", ".vsdx", ".vsdm", ".vstx", ".vstm", ".odg", ".dxf", ".svg",
        // Immagini
        ".png", ".jpg", ".jpeg", ".jpe", ".gif", ".bmp", ".tif", ".tiff", ".webp",
    ];

    private readonly string _soffice;
    private readonly TimeSpan _timeout;
    private readonly SemaphoreSlim _slots;
    private readonly ConcurrentQueue<string> _profiles = new();

    public string ExecutablePath => _soffice;

    public string Description => $"LibreOffice ({_soffice})";

    private LibreOfficeConverter(string soffice, EngineSettings settings)
    {
        _soffice = soffice;
        _timeout = settings.ConversionTimeout;
        int slots = Math.Max(1, settings.MaxParallelConversions);
        _slots = new SemaphoreSlim(slots, slots);

        var root = Path.Combine(settings.WorkDirectory, "lo-profiles");
        for (int i = 0; i < slots; i++)
            _profiles.Enqueue(Path.Combine(root, $"p{i}"));
    }

    public static LibreOfficeConverter? TryCreate(EngineSettings settings)
    {
        var path = ToolLocator.FindLibreOffice(settings.LibreOfficePath);
        return path is null ? null : new LibreOfficeConverter(path, settings);
    }

    public static bool CanConvert(string extension) => SupportedExtensions.Contains(extension.ToLowerInvariant());

    /// <param name="sourceExtension">Estensione reale del file: LibreOffice sceglie il filtro di importazione in base a questa.</param>
    public void ConvertToPdf(string inputPath, string outputPdfPath, string sourceExtension)
    {
        var work = Path.Combine(Path.GetTempPath(), "wm-lo-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);

        _slots.Wait();
        _profiles.TryDequeue(out var profile);
        try
        {
            var source = Path.Combine(work, "document" + sourceExtension);
            File.Copy(inputPath, source);

            Directory.CreateDirectory(profile!);
            var args = new[]
            {
                "-env:UserInstallation=" + new Uri(profile!).AbsoluteUri,
                "--headless", "--invisible", "--nologo", "--nodefault", "--norestore", "--nolockcheck",
                "--convert-to", "pdf",
                "--outdir", work,
                source,
            };

            var result = ProcessRunner.Run(_soffice, args, _timeout, work);
            var pdf = Path.Combine(work, "document.pdf");

            if (!File.Exists(pdf))
                throw new InvalidOperationException(
                    "LibreOffice did not produce a PDF. " +
                    (string.IsNullOrWhiteSpace(result.StdErr) ? $"Exit code {result.ExitCode}." : result.StdErr.Trim()));

            File.Move(pdf, outputPdfPath, overwrite: true);
        }
        finally
        {
            if (profile is not null) _profiles.Enqueue(profile);
            _slots.Release();
            try { Directory.Delete(work, recursive: true); } catch { /* pulizia best effort */ }
        }
    }
}
