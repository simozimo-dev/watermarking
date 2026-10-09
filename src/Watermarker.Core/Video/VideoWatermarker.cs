using System.Globalization;
using Watermarker.Core.Conversion;
using Watermarker.Core.Rendering;

namespace Watermarker.Core.Video;

/// <summary>
/// Watermark per video con FFmpeg: il testo viene fuso in ogni fotogramma (ricodifica),
/// quindi non è rimovibile senza alterare il video. L'audio viene copiato senza ricodifica.
/// Disponibile solo se FFmpeg e ffprobe sono installati.
/// </summary>
public sealed class VideoWatermarker : IWatermarker
{
    private readonly string? _ffmpeg;
    private readonly string? _ffprobe;
    private readonly TimeSpan _timeout;

    public VideoWatermarker(EngineSettings settings)
    {
        _ffmpeg = ToolLocator.FindFfmpeg(settings.FfmpegPath);
        _ffprobe = ToolLocator.FindFfprobe(_ffmpeg);
        _timeout = settings.VideoTimeout;
    }

    public IReadOnlyCollection<string> Extensions { get; } = [".mp4", ".m4v", ".mov", ".mkv", ".webm", ".avi"];

    public bool IsAvailable => _ffmpeg is not null && _ffprobe is not null;

    public WatermarkResult Apply(string inputPath, string outputPath, WatermarkOptions options)
    {
        if (!IsAvailable) throw new InvalidOperationException("FFmpeg is not installed: videos cannot be processed.");

        var (width, height) = Probe(inputPath);
        var overlay = Path.Combine(Path.GetTempPath(), $"wm-{Guid.NewGuid():N}.png");
        try
        {
            File.WriteAllBytes(overlay, WatermarkImage.RenderFrame(width, height, options));

            var ext = Path.GetExtension(outputPath).ToLowerInvariant();
            var args = new List<string>
            {
                "-y", "-v", "error", "-nostdin",
                "-i", inputPath,
                "-i", overlay,
                "-filter_complex", "[0:v:0][1:v]overlay=0:0:format=auto,format=yuv420p[v]",
                "-map", "[v]", "-map", "0:a?",
                "-map_metadata", "0",
            };

            if (ext == ".webm") args.AddRange(["-c:v", "libvpx-vp9", "-crf", "32", "-b:v", "0", "-row-mt", "1"]);
            else args.AddRange(["-c:v", "libx264", "-crf", "18", "-preset", "medium"]);

            args.AddRange(["-c:a", "copy"]);
            if (ext is ".mp4" or ".m4v" or ".mov") args.AddRange(["-movflags", "+faststart"]);
            args.Add(outputPath);

            var result = ProcessRunner.Run(_ffmpeg!, args, _timeout);
            if (result.ExitCode != 0 || !File.Exists(outputPath))
                throw new InvalidOperationException("FFmpeg returned an error: " + result.StdErr.Trim());

            return WatermarkResult.None;
        }
        finally
        {
            File.Delete(overlay);
        }
    }

    /// <summary>Dimensioni visualizzate del video, tenendo conto della rotazione nei metadati.</summary>
    private (int Width, int Height) Probe(string input)
    {
        var result = ProcessRunner.Run(_ffprobe!,
            ["-v", "error", "-select_streams", "v:0",
             "-show_entries", "stream=width,height:stream_side_data=rotation",
             "-of", "default=noprint_wrappers=1", input],
            TimeSpan.FromMinutes(1));

        int w = 0, h = 0, rotation = 0;
        foreach (var line in result.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split('=', 2);
            if (parts.Length != 2) continue;
            _ = parts[0] switch
            {
                "width" => int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out w),
                "height" => int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out h),
                "rotation" => int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out rotation),
                _ => false,
            };
        }

        if (w <= 0 || h <= 0) throw new InvalidDataException("The file does not contain a readable video track.");
        return Math.Abs(rotation) % 180 == 90 ? (h, w) : (w, h);
    }
}
