using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Watermarker.Core.Conversion;

public sealed record ProcessOutput(int ExitCode, string StdOut, string StdErr);

/// <summary>Esecuzione di programmi esterni (LibreOffice, FFmpeg) con timeout e terminazione garantita.</summary>
internal static class ProcessRunner
{
    public static ProcessOutput Run(string fileName, IEnumerable<string> arguments, TimeSpan timeout, string? workingDirectory = null)
    {
        var psi = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory ?? Environment.CurrentDirectory,
        };
        foreach (var a in arguments) psi.ArgumentList.Add(a);

        using var process = new Process { StartInfo = psi };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (stdout) stdout.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (stderr) stderr.AppendLine(e.Data); };

        if (!process.Start()) throw new InvalidOperationException($"Cannot start {fileName}.");
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        if (!process.WaitForExit((int)Math.Min(int.MaxValue, timeout.TotalMilliseconds)))
        {
            try { process.Kill(entireProcessTree: true); } catch { /* già terminato */ }
            throw new TimeoutException($"{Path.GetFileNameWithoutExtension(fileName)} did not finish within {timeout.TotalSeconds:0} seconds.");
        }
        process.WaitForExit(); // svuota i buffer degli eventi

        return new ProcessOutput(process.ExitCode, stdout.ToString(), stderr.ToString());
    }
}

/// <summary>Trova gli eseguibili esterni: percorso configurato, posizioni tipiche, poi PATH.</summary>
public static class ToolLocator
{
    private static bool IsWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    public static string? FindLibreOffice(string? configured) => Find(configured, "soffice",
        IsWindows
            ? [
                @"C:\Program Files\LibreOffice\program\soffice.exe",
                @"C:\Program Files (x86)\LibreOffice\program\soffice.exe",
              ]
            : [
                "/usr/bin/soffice",
                "/usr/lib/libreoffice/program/soffice",
                "/opt/libreoffice/program/soffice",
                "/Applications/LibreOffice.app/Contents/MacOS/soffice",
              ]);

    public static string? FindFfmpeg(string? configured) => Find(configured, "ffmpeg",
        IsWindows ? [@"C:\ffmpeg\bin\ffmpeg.exe"] : ["/usr/bin/ffmpeg", "/usr/local/bin/ffmpeg", "/opt/homebrew/bin/ffmpeg"]);

    /// <summary>ffprobe sta normalmente nella stessa cartella di ffmpeg.</summary>
    public static string? FindFfprobe(string? ffmpegPath)
    {
        if (ffmpegPath is null) return null;
        var dir = Path.GetDirectoryName(ffmpegPath);
        var candidate = Path.Combine(dir ?? "", IsWindows ? "ffprobe.exe" : "ffprobe");
        return File.Exists(candidate) ? candidate : Find(null, "ffprobe", []);
    }

    private static string? Find(string? configured, string exeName, string[] candidates)
    {
        if (!string.IsNullOrWhiteSpace(configured))
            return File.Exists(configured) ? configured : null;

        foreach (var c in candidates)
            if (File.Exists(c)) return c;

        var fileName = IsWindows ? exeName + ".exe" : exeName;
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            try
            {
                var full = Path.Combine(dir.Trim('"'), fileName);
                if (File.Exists(full)) return full;
            }
            catch (ArgumentException) { /* voce di PATH non valida */ }
        }
        return null;
    }
}
