using Watermarker.Core.Containers;
using Watermarker.Core.Conversion;
using Watermarker.Core.Formats;
using Watermarker.Core.Images;
using Watermarker.Core.Office;
using Watermarker.Core.OpenDocument;
using Watermarker.Core.Pdf;
using Watermarker.Core.Video;
using Watermarker.Core.Visio;
using Watermarker.Core.Xps;

namespace Watermarker.Core;

public interface IWatermarker
{
    /// <summary>Estensioni gestite, in minuscolo e con il punto (".pdf").</summary>
    IReadOnlyCollection<string> Extensions { get; }

    /// <summary>False se manca un programma esterno necessario (per esempio FFmpeg).</summary>
    bool IsAvailable => true;

    /// <summary>Legge <paramref name="inputPath"/> e scrive il file con watermark in <paramref name="outputPath"/>.</summary>
    WatermarkResult Apply(string inputPath, string outputPath, WatermarkOptions options);
}

/// <summary>Configurazione del motore: programmi esterni e limiti.</summary>
public sealed record EngineSettings
{
    /// <summary>Percorso di soffice (LibreOffice). Se vuoto viene cercato automaticamente.</summary>
    public string? LibreOfficePath { get; init; }

    /// <summary>
    /// URL di un servizio Gotenberg (es. http://converter:3000). Se impostato, la conversione in PDF
    /// avviene lì invece che con un LibreOffice locale.
    /// </summary>
    public string? ConverterUrl { get; init; }

    /// <summary>Percorso di ffmpeg. Se vuoto viene cercato automaticamente.</summary>
    public string? FfmpegPath { get; init; }

    /// <summary>Cartella per i file temporanei e i profili di LibreOffice.</summary>
    public string WorkDirectory { get; init; } = Path.Combine(Path.GetTempPath(), "watermarker");

    public int MaxParallelConversions { get; init; } = 2;
    public TimeSpan ConversionTimeout { get; init; } = TimeSpan.FromMinutes(2);
    public TimeSpan VideoTimeout { get; init; } = TimeSpan.FromMinutes(30);

    public int MaxContainerDepth { get; init; } = 3;
    public int MaxContainerEntries { get; init; } = 2000;
    public long MaxContainerBytes { get; init; } = 1L << 30;
}

public sealed record ProcessResult(DetectedFormat Format, bool ConvertedToPdf, IReadOnlyList<string> Warnings);

/// <summary>
/// Punto di ingresso unico: riconosce il formato dal contenuto, sceglie il watermarker,
/// converte in PDF se richiesto e scrive l'output in modo atomico.
/// Un'istanza può essere condivisa tra più richieste (thread-safe).
/// </summary>
public sealed class WatermarkEngine
{
    private readonly IWatermarker[] _watermarkers;
    private readonly PdfWatermarker _pdf = new();

    public EngineSettings Settings { get; }

    /// <summary>Null se non c'è né un servizio di conversione né LibreOffice: la conversione in PDF non è disponibile.</summary>
    public IPdfConverter? Converter { get; }

    public WatermarkEngine(EngineSettings? settings = null)
    {
        Settings = settings ?? new EngineSettings();
        if (string.IsNullOrWhiteSpace(Settings.WorkDirectory))
            Settings = Settings with { WorkDirectory = Path.Combine(Path.GetTempPath(), "watermarker") };
        Directory.CreateDirectory(Settings.WorkDirectory);

        _watermarkers =
        [
            _pdf,
            new WordWatermarker(),
            new ExcelWatermarker(),
            new PowerPointWatermarker(),
            new OpenDocumentWatermarker(),
            new VisioWatermarker(),
            new XpsWatermarker(),
            new ImageWatermarker(),
            new VideoWatermarker(Settings),
            new ZipWatermarker(this),
            new EmailWatermarker(this),
        ];

        Converter = !string.IsNullOrWhiteSpace(Settings.ConverterUrl)
            ? new GotenbergConverter(Settings.ConverterUrl, Settings.ConversionTimeout)
            : LibreOfficeConverter.TryCreate(Settings);
    }

    /// <summary>Estensioni elaborabili mantenendo il formato originale.</summary>
    public IReadOnlyList<string> NativeExtensions =>
        _watermarkers.Where(w => w.IsAvailable).SelectMany(w => w.Extensions).Distinct().ToList();

    /// <summary>Estensioni convertibili in PDF (vuoto se LibreOffice non è disponibile).</summary>
    public IReadOnlyList<string> PdfExtensions =>
        Converter is null ? [".pdf"] : LibreOfficeConverter.SupportedExtensions.Append(".pdf").Distinct().ToList();

    public DetectedFormat Detect(string path, string? originalName = null) => FormatDetector.Detect(path, originalName);

    /// <summary>Watermarker nativo per il formato, oppure null.</summary>
    public IWatermarker? FindNative(DetectedFormat format) =>
        format.Family is FormatFamily.Unknown or FormatFamily.Unsupported or FormatFamily.ConvertibleOnly
            ? null
            : _watermarkers.FirstOrDefault(w => w.IsAvailable && w.Extensions.Contains(format.Extension));

    public bool CanConvertToPdf(DetectedFormat format) =>
        format.Family == FormatFamily.Pdf
        || (Converter is not null && LibreOfficeConverter.CanConvert(format.Extension));

    /// <summary>Nome suggerito per il file prodotto (es. "contratto_watermark.pdf").</summary>
    public static string SuggestOutputName(string originalFileName, DetectedFormat format, OutputMode mode)
    {
        var baseName = Path.GetFileNameWithoutExtension(originalFileName);
        if (string.IsNullOrWhiteSpace(baseName)) baseName = "document";
        var originalExt = Path.GetExtension(originalFileName).ToLowerInvariant();

        var ext = mode == OutputMode.Pdf
            ? ".pdf"
            : format.ExtensionMismatch || string.IsNullOrEmpty(originalExt) ? format.Extension : originalExt;
        return $"{baseName}_watermark{ext}";
    }

    /// <summary>
    /// Elabora un file. <paramref name="originalFileName"/> serve solo a riconoscere i formati
    /// testuali (txt, csv, eml...) quando il file su disco ha un nome temporaneo.
    /// </summary>
    public ProcessResult Process(string inputPath, string outputPath, WatermarkOptions options,
                                 OutputMode mode = OutputMode.Original, string? originalFileName = null)
    {
        options.Validate();
        if (!File.Exists(inputPath))
            throw new FileNotFoundException($"File not found: {inputPath}", inputPath);

        var format = Detect(inputPath, originalFileName ?? inputPath);
        var warnings = new List<string>();

        if (format.ExtensionMismatch)
            warnings.Add($"The file extension does not match its content: processed as {format.Description} ({format.Extension}).");
        if (format.HasDigitalSignature)
            warnings.Add("The document was digitally signed: the signature is no longer valid after watermarking.");

        var fullOutput = Path.GetFullPath(outputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullOutput)!);

        bool convert = mode == OutputMode.Pdf && format.Family != FormatFamily.Pdf;
        var tmpExt = mode == OutputMode.Pdf ? ".pdf" : format.Extension;
        var tmp = Path.Combine(Settings.WorkDirectory, $"{Guid.NewGuid():N}{tmpExt}");
        var tmpPdf = Path.Combine(Settings.WorkDirectory, $"{Guid.NewGuid():N}.src.pdf");

        try
        {
            WatermarkResult result;
            if (convert)
            {
                if (Converter is null)
                    throw new NotSupportedException("PDF conversion requires LibreOffice, which is not installed.");
                if (!LibreOfficeConverter.CanConvert(format.Extension))
                    throw new NotSupportedException($"{format.Description}: PDF conversion is not available.");

                Converter.ConvertToPdf(inputPath, tmpPdf, format.Extension);
                result = _pdf.Apply(tmpPdf, tmp, options);
            }
            else
            {
                result = ApplyNative(inputPath, tmp, options, format);
            }

            warnings.AddRange(result.Warnings);
            File.Move(tmp, fullOutput, overwrite: true);
            return new ProcessResult(format, convert, warnings);
        }
        finally
        {
            if (File.Exists(tmp)) File.Delete(tmp);
            if (File.Exists(tmpPdf)) File.Delete(tmpPdf);
        }
    }

    /// <summary>Applica il watermark mantenendo il formato. Usato anche per i file dentro ZIP ed email.</summary>
    internal WatermarkResult ApplyNative(string inputPath, string outputPath, WatermarkOptions options, DetectedFormat format)
    {
        var watermarker = FindNative(format);
        if (watermarker is not null) return watermarker.Apply(inputPath, outputPath, options);

        var hint = CanConvertToPdf(format) ? " It can be processed by converting it to PDF." : "";
        throw format.Family switch
        {
            FormatFamily.Video => new NotSupportedException("Videos require FFmpeg, which is not installed."),
            FormatFamily.ConvertibleOnly => new NotSupportedException($"{format.Description}: the original format cannot be kept.{hint}"),
            FormatFamily.Unknown => new NotSupportedException($"Unrecognized format ({format.Extension}).{hint}"),
            _ => new NotSupportedException($"{format.Description}: format not supported.{hint}"),
        };
    }
}
