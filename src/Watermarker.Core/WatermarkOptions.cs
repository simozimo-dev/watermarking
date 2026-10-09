using System.Globalization;

namespace Watermarker.Core;

/// <summary>Colore RGBA; l'alpha rappresenta l'opacità del watermark.</summary>
public readonly record struct RgbaColor(byte R, byte G, byte B, byte A)
{
    public double Opacity => A / 255.0;

    /// <summary>Colore senza alpha nel formato RRGGBB (senza #).</summary>
    public string RgbHex => $"{R:X2}{G:X2}{B:X2}";

    public RgbaColor WithOpacity(double opacity) =>
        this with { A = (byte)Math.Round(Math.Clamp(opacity, 0, 1) * 255) };

    private static readonly Dictionary<string, string> Named = new(StringComparer.OrdinalIgnoreCase)
    {
        ["black"] = "000000", ["gray"] = "808080", ["grey"] = "808080", ["red"] = "FF0000",
        ["blue"] = "0000FF", ["green"] = "008000", ["white"] = "FFFFFF", ["orange"] = "FFA500",
    };

    /// <summary>Accetta #RRGGBB, #RRGGBBAA o alcuni nomi (red, gray, blue...).</summary>
    public static RgbaColor Parse(string value)
    {
        var s = value.Trim();
        if (Named.TryGetValue(s, out var hex)) s = hex;
        s = s.TrimStart('#');

        if (s.Length is not (6 or 8) ||
            !uint.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v))
            throw new FormatException($"Invalid color: '{value}'. Use #RRGGBB or #RRGGBBAA.");

        if (s.Length == 6) v = (v << 8) | 0xFF;
        return new RgbaColor((byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v);
    }
}

public sealed record WatermarkOptions
{
    /// <summary>Testo del watermark. Può contenere più righe, separate da a capo (\n).</summary>
    public required string Text { get; init; }

    /// <summary>Numero massimo di righe del testo.</summary>
    public const int MaxLines = 10;

    /// <summary>Inclinazione in gradi, in senso antiorario (45 = diagonale dal basso a sinistra all'alto a destra).</summary>
    public double AngleDegrees { get; init; } = 45;

    /// <summary>Dimensione del testo in punti tipografici.</summary>
    public double FontSize { get; init; } = 60;

    /// <summary>Colore con opacità (alpha).</summary>
    public RgbaColor Color { get; init; } = new(0x80, 0x80, 0x80, 0x59);

    /// <summary>Nome della famiglia di caratteri oppure percorso di un file .ttf/.otf.</summary>
    public string Font { get; init; } = "Arial";

    /// <summary>Ripete il watermark a griglia su tutta la pagina (PDF).</summary>
    public bool Tile { get; init; }

    /// <summary>PowerPoint: oltre a master e layout, mette il watermark in primo piano su ogni slide.</summary>
    public bool PowerPointOnSlides { get; init; }

    /// <summary>Profondità di annidamento (file dentro ZIP o email). Gestita dal motore.</summary>
    internal int Depth { get; init; }

    /// <summary>Le stesse opzioni con il testo ridimensionato (per immagini e video).</summary>
    internal WatermarkOptions Scaled(double factor) => this with { FontSize = FontSize * factor };

    /// <summary>Angolo normalizzato in [0, 360).</summary>
    public double NormalizedAngle => ((AngleDegrees % 360) + 360) % 360;

    /// <summary>Angolo in senso orario, come lo vogliono VML e DrawingML.</summary>
    public double ClockwiseDegrees => (360 - NormalizedAngle) % 360;

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Text))
            throw new ArgumentException("The watermark text is required.");
        if (Rendering.FontLoader.Lines(Text).Length > MaxLines)
            throw new ArgumentException($"The watermark can have at most {MaxLines} lines.");
        if (FontSize is <= 0 or > 1000)
            throw new ArgumentException("The size must be between 0 and 1000 points.");
        if (Color.A == 0)
            throw new ArgumentException("Opacity is 0: the watermark would be invisible.");
    }
}

public sealed record WatermarkResult(IReadOnlyList<string> Warnings)
{
    public static WatermarkResult None { get; } = new(Array.Empty<string>());
}

/// <summary>Formato del file prodotto.</summary>
public enum OutputMode
{
    /// <summary>Stesso formato del file caricato.</summary>
    Original,

    /// <summary>Conversione in PDF (tramite LibreOffice) e poi watermark.</summary>
    Pdf,
}
