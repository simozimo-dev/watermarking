using ImageMagick;
using Watermarker.Core.Rendering;

namespace Watermarker.Core.Images;

/// <summary>
/// Watermark per immagini: il testo viene fuso nei pixel, quindi non esiste un oggetto
/// separato da togliere. È la forma di watermark più resistente tra quelle supportate.
///
///  - TIFF multipagina: watermark su ogni pagina.
///  - GIF e WebP animati: watermark su ogni fotogramma, l'animazione resta.
///  - JPEG: qualità e metadati EXIF originali mantenuti; l'orientamento EXIF viene applicato
///    ai pixel, così il watermark risulta dritto a prescindere da come era stata scattata la foto.
///
/// La dimensione del testo è proporzionale all'immagine (vedi <see cref="WatermarkImage.ReferencePagePt"/>).
/// </summary>
public sealed class ImageWatermarker : IWatermarker
{
    public IReadOnlyCollection<string> Extensions { get; } =
        [".png", ".jpg", ".jpeg", ".jpe", ".gif", ".bmp", ".tif", ".tiff", ".webp"];

    public WatermarkResult Apply(string inputPath, string outputPath, WatermarkOptions options)
    {
        var warnings = new List<string>();

        using var frames = new MagickImageCollection(inputPath);
        if (frames.Count == 0) throw new InvalidDataException("The image has no content.");

        var format = frames[0].Format;
        bool multiFrame = frames.Count > 1;
        bool animated = multiFrame && format is MagickFormat.Gif or MagickFormat.WebP;

        // Nelle animazioni i fotogrammi possono essere parziali: li ricostruiamo interi.
        if (animated) frames.Coalesce();

        byte[]? cachedOverlay = null;
        (int W, int H) cachedSize = default;

        foreach (var frame in frames)
        {
            if (!multiFrame) frame.AutoOrient();

            if (frame.ColorSpace == ColorSpace.CMYK)
                warnings.Add("CMYK image: watermark colors may differ slightly.");

            int w = (int)frame.Width, h = (int)frame.Height;
            if (cachedOverlay is null || cachedSize != (w, h))
            {
                cachedOverlay = WatermarkImage.RenderFrame(w, h, options);
                cachedSize = (w, h);
            }

            using var overlay = new MagickImage(cachedOverlay);
            frame.Composite(overlay, 0, 0, CompositeOperator.Over);
        }

        frames.Write(outputPath, format);
        return new WatermarkResult(warnings.Distinct().ToList());
    }
}
