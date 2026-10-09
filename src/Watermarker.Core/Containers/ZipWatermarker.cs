using System.IO.Compression;
using Watermarker.Core.Formats;

namespace Watermarker.Core.Containers;

/// <summary>
/// Archivi ZIP: ogni file supportato contenuto nell'archivio riceve il watermark (anche ZIP dentro ZIP,
/// fino alla profondità configurata). I file non supportati restano invariati e vengono segnalati.
/// Struttura, nomi e date delle voci sono mantenuti. Limiti anti "zip bomb" su numero di voci e dimensione.
/// </summary>
public sealed class ZipWatermarker(WatermarkEngine engine) : IWatermarker
{
    public IReadOnlyCollection<string> Extensions { get; } = [".zip"];

    public WatermarkResult Apply(string inputPath, string outputPath, WatermarkOptions options)
    {
        var settings = engine.Settings;
        if (options.Depth >= settings.MaxContainerDepth)
            throw new InvalidDataException($"Too many nested archive levels (maximum {settings.MaxContainerDepth}).");

        var warnings = new List<string>();
        var skipped = new List<string>();
        var work = Path.Combine(Path.GetTempPath(), "wm-zip-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);

        try
        {
            using var source = ZipFile.OpenRead(inputPath);
            if (source.Entries.Count > settings.MaxContainerEntries)
                throw new InvalidDataException($"The archive contains more than {settings.MaxContainerEntries} files.");

            using var outStream = File.Create(outputPath);
            using var target = new ZipArchive(outStream, ZipArchiveMode.Create);
            long budget = settings.MaxContainerBytes;
            int index = 0;

            foreach (var entry in source.Entries)
            {
                index++;
                var newEntry = target.CreateEntry(entry.FullName, CompressionLevel.Optimal);
                newEntry.LastWriteTime = entry.LastWriteTime;
                newEntry.ExternalAttributes = entry.ExternalAttributes;
                if (entry.FullName.EndsWith('/')) continue; // cartella

                var extracted = Path.Combine(work, $"{index}.in");
                using (var src = OpenEntry(entry))
                using (var dst = File.Create(extracted))
                    budget -= CopyLimited(src, dst, budget);

                var fmt = FormatDetector.Detect(extracted, entry.Name);
                string result = extracted;

                if (engine.FindNative(fmt) is not null)
                {
                    var processed = Path.Combine(work, $"{index}.out{fmt.Extension}");
                    try
                    {
                        var r = engine.ApplyNative(extracted, processed, options with { Depth = options.Depth + 1 }, fmt);
                        warnings.AddRange(r.Warnings.Select(w => $"{entry.FullName}: {w}"));
                        result = processed;
                    }
                    catch (Exception ex) when (ex is not OutOfMemoryException)
                    {
                        warnings.Add($"{entry.FullName}: watermark not applied ({ex.Message}).");
                    }
                }
                else
                {
                    skipped.Add(entry.FullName);
                }

                using (var dst = newEntry.Open())
                using (var src = File.OpenRead(result))
                    src.CopyTo(dst);

                File.Delete(extracted);
                if (result != extracted) File.Delete(result);
            }
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); } catch { /* best effort */ }
        }

        if (skipped.Count > 0)
            warnings.Add($"{skipped.Count} file(s) in unsupported formats left without a watermark: " +
                         string.Join(", ", skipped.Take(10)) + (skipped.Count > 10 ? ", ..." : "."));

        return new WatermarkResult(warnings);
    }

    private static Stream OpenEntry(ZipArchiveEntry entry)
    {
        try { return entry.Open(); }
        catch (Exception ex) when (ex is InvalidDataException or NotSupportedException)
        {
            throw new InvalidDataException($"Cannot read '{entry.FullName}': encrypted archive or unsupported compression.");
        }
    }

    /// <summary>Copia rispettando il budget complessivo: la dimensione dichiarata nell'archivio può essere falsa.</summary>
    private static long CopyLimited(Stream src, Stream dst, long budget)
    {
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = src.Read(buffer)) > 0)
        {
            total += read;
            if (total > budget)
                throw new InvalidDataException("The archive exceeds the maximum allowed size when uncompressed.");
            dst.Write(buffer, 0, read);
        }
        return total;
    }
}
