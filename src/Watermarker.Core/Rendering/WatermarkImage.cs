using SkiaSharp;

namespace Watermarker.Core.Rendering;

internal sealed record RenderedImage(byte[] Png, int WidthPx, int HeightPx, float Dpi)
{
    public double WidthPt => WidthPx * 72.0 / Dpi;
    public double HeightPt => HeightPx * 72.0 / Dpi;
}

/// <summary>Disegna il testo ruotato e semitrasparente su un PNG con sfondo trasparente.</summary>
internal static class WatermarkImage
{
    /// <param name="gapFactor">Margine attorno al testo, in multipli della dimensione del font
    /// (per le immagini ripetute a mosaico serve un margine più ampio).</param>
    public static RenderedImage Render(WatermarkOptions o, double gapFactor, float dpi = 96f)
    {
        float scale = dpi / 72f;
        using var font = new SKFont(FontLoader.Load(o.Font), (float)(o.FontSize * scale))
        {
            Edging = SKFontEdging.Antialias,
        };

        var lines = FontLoader.Lines(o.Text);
        var (textW, textH) = BlockSize(lines, font);

        double rad = o.AngleDegrees * Math.PI / 180.0;
        double cos = Math.Abs(Math.Cos(rad)), sin = Math.Abs(Math.Sin(rad));
        double gap = o.FontSize * scale * gapFactor;

        int w = Math.Clamp((int)Math.Ceiling(textW * cos + textH * sin + gap), 1, 8000);
        int h = Math.Clamp((int)Math.Ceiling(textW * sin + textH * cos + gap), 1, 8000);

        using var surface = SKSurface.Create(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul));
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Transparent);
        canvas.Translate(w / 2f, h / 2f);
        canvas.RotateDegrees((float)-o.AngleDegrees); // Skia ruota in senso orario

        using var paint = new SKPaint
        {
            Color = new SKColor(o.Color.R, o.Color.G, o.Color.B, o.Color.A),
            IsAntialias = true,
        };
        DrawBlock(canvas, lines, font, paint, 0, 0);

        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return new RenderedImage(data.ToArray(), w, h, dpi);
    }

    /// <summary>
    /// Lato lungo di una pagina A4 in punti. Per immagini e video la dimensione del testo
    /// è proporzionale: un watermark di 60 pt occupa sull'immagine la stessa frazione che
    /// occuperebbe su una pagina A4.
    /// </summary>
    public const double ReferencePagePt = 842;

    /// <summary>
    /// Disegna il watermark su un livello trasparente grande quanto l'immagine o il fotogramma,
    /// centrato o ripetuto a griglia, da sovrapporre al contenuto.
    /// </summary>
    public static byte[] RenderFrame(int width, int height, WatermarkOptions o)
    {
        double pxPerPt = Math.Max(width, height) / ReferencePagePt;
        float fontPx = (float)Math.Max(4, o.FontSize * pxPerPt);

        using var font = new SKFont(FontLoader.Load(o.Font), fontPx) { Edging = SKFontEdging.Antialias };
        var lines = FontLoader.Lines(o.Text);
        var (textW, textH) = BlockSize(lines, font);

        using var surface = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Transparent);
        canvas.Translate(width / 2f, height / 2f);
        canvas.RotateDegrees((float)-o.AngleDegrees);

        using var paint = new SKPaint
        {
            Color = new SKColor(o.Color.R, o.Color.G, o.Color.B, o.Color.A),
            IsAntialias = true,
        };

        if (!o.Tile)
        {
            DrawBlock(canvas, lines, font, paint, 0, 0);
        }
        else
        {
            float stepX = textW + fontPx * 1.5f;
            float stepY = textH + fontPx * 3f;
            float radius = (float)Math.Sqrt((double)width * width + (double)height * height) / 2f + Math.Max(stepX, stepY);
            int row = 0;
            for (float y = -radius; y <= radius; y += stepY, row++)
            {
                float offset = row % 2 == 0 ? 0 : stepX / 2f;
                for (float x = -radius + offset; x <= radius; x += stepX)
                    DrawBlock(canvas, lines, font, paint, x, y);
            }
        }

        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    /// <summary>Larghezza della riga più lunga e altezza complessiva del blocco di righe, in pixel.</summary>
    private static (float Width, float Height) BlockSize(string[] lines, SKFont font)
    {
        font.GetFontMetrics(out var m);
        float width = lines.Max(l => font.MeasureText(l));
        float height = (float)((lines.Length - 1) * font.Size * FontLoader.LineSpacing) + (m.Descent - m.Ascent);
        return (width, height);
    }

    /// <summary>Disegna le righe centrate attorno al punto (cx, cy), una sotto l'altra.</summary>
    private static void DrawBlock(SKCanvas canvas, string[] lines, SKFont font, SKPaint paint, float cx, float cy)
    {
        font.GetFontMetrics(out var m);
        float baseline = -(m.Ascent + m.Descent) / 2f;
        for (int i = 0; i < lines.Length; i++)
        {
            float w = font.MeasureText(lines[i]);
            float y = cy + (float)FontLoader.LineOffset(i, lines.Length, font.Size) + baseline;
            canvas.DrawText(lines[i], cx - w / 2f, y, font, paint);
        }
    }
}
