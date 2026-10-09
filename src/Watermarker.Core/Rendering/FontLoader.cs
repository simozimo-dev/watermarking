using SkiaSharp;

namespace Watermarker.Core.Rendering;

internal static class FontLoader
{
    /// <summary>Carica un font da nome di famiglia o da file. Se il font non esiste usa quello di sistema.</summary>
    public static SKTypeface Load(string font)
    {
        if (File.Exists(font))
            return SKTypeface.FromFile(font)
                ?? throw new InvalidOperationException($"Cannot read the font file '{font}'.");

        return SKTypeface.FromFamilyName(font, SKFontStyle.Bold) ?? SKTypeface.Default;
    }

    /// <summary>Nome di famiglia da scrivere nei file Office (che referenziano il font per nome).</summary>
    public static string FamilyName(string font) =>
        File.Exists(font) ? Load(font).FamilyName : font;

    /// <summary>Distanza tra le righe di un watermark multilinea, in multipli della dimensione del testo.</summary>
    public const double LineSpacing = 1.2;

    /// <summary>Righe del testo (a capo con \n o \r\n); le righe vuote vengono ignorate.</summary>
    public static string[] Lines(string text) =>
        text.Replace("\r\n", "\n").Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length > 0)
            .DefaultIfEmpty(text.Trim())
            .ToArray();

    /// <summary>Larghezza di avanzamento della riga più lunga, in punti.</summary>
    public static double MeasureWidth(string text, string font, double fontSize)
    {
        using var f = new SKFont(Load(font), (float)fontSize);
        return Lines(text).Max(l => f.MeasureText(l));
    }

    /// <summary>
    /// Scostamento verticale del centro della riga <paramref name="index"/> rispetto al centro del blocco,
    /// in punti (positivo verso il basso).
    /// </summary>
    public static double LineOffset(int index, int count, double fontSize) =>
        (index - (count - 1) / 2.0) * fontSize * LineSpacing;
}
