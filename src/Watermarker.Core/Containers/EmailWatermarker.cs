using MimeKit;
using MimeKit.Cryptography;
using Watermarker.Core.Formats;

namespace Watermarker.Core.Containers;

/// <summary>
/// Email in formato .eml: il watermark viene applicato a ogni allegato supportato, anche negli
/// allegati di messaggi inoltrati. Testo, mittenti e intestazioni non vengono toccati.
/// Le email firmate (S/MIME) perdono la validità della firma; quelle cifrate non sono elaborabili.
/// </summary>
public sealed class EmailWatermarker(WatermarkEngine engine) : IWatermarker
{
    public IReadOnlyCollection<string> Extensions { get; } = [".eml"];

    public WatermarkResult Apply(string inputPath, string outputPath, WatermarkOptions options)
    {
        if (options.Depth >= engine.Settings.MaxContainerDepth)
            throw new InvalidDataException("Too many nested message levels.");

        var warnings = new List<string>();
        var message = MimeMessage.Load(inputPath);

        int processed = Process(message, options, warnings, prefix: "");
        if (processed == 0) warnings.Add("No supported attachments: the email was left unchanged.");

        message.WriteTo(outputPath);
        return new WatermarkResult(warnings);
    }

    private int Process(MimeMessage message, WatermarkOptions options, List<string> warnings, string prefix)
    {
        if (message.Body is MultipartSigned)
            warnings.Add($"{prefix}Digitally signed message: the signature will no longer be valid.");
        if (message.Body is ApplicationPkcs7Mime or MultipartEncrypted)
        {
            warnings.Add($"{prefix}Encrypted message: attachments cannot be processed.");
            return 0;
        }

        int count = 0;
        int index = 0;

        foreach (var entity in message.BodyParts.ToList())
        {
            index++;
            if (entity is MessagePart { Message: { } inner })
            {
                count += Process(inner, options, warnings, $"{prefix}[attached message] ");
                continue;
            }

            if (entity is not MimePart part || part.Content is null) continue;
            if (!part.IsAttachment && string.IsNullOrEmpty(part.FileName)) continue; // corpo del messaggio

            var name = part.FileName ?? $"attachment{index}";
            var work = Path.Combine(Path.GetTempPath(), "wm-eml-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(work);
            try
            {
                var extracted = Path.Combine(work, "in");
                using (var fs = File.Create(extracted)) part.Content.DecodeTo(fs);

                var fmt = FormatDetector.Detect(extracted, name);
                if (engine.FindNative(fmt) is null)
                {
                    warnings.Add($"{prefix}{name}: unsupported format, left without a watermark.");
                    continue;
                }

                var output = Path.Combine(work, "out" + fmt.Extension);
                var r = engine.ApplyNative(extracted, output, options with { Depth = options.Depth + 1 }, fmt);
                warnings.AddRange(r.Warnings.Select(w => $"{prefix}{name}: {w}"));

                part.Content = new MimeContent(new MemoryStream(File.ReadAllBytes(output)));
                part.ContentTransferEncoding = ContentEncoding.Base64;
                count++;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                warnings.Add($"{prefix}{name}: watermark not applied ({ex.Message}).");
            }
            finally
            {
                try { Directory.Delete(work, recursive: true); } catch { /* best effort */ }
            }
        }

        return count;
    }
}
