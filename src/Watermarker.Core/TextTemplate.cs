using System.Globalization;
using System.Text.RegularExpressions;

namespace Watermarker.Core;

/// <summary>
/// Segnaposto nel testo del watermark, sostituiti al momento dell'elaborazione:
/// {data}, {ora}, {nomefile}, {utente}, {email}. Sono accettati anche gli equivalenti inglesi
/// {date}, {time}, {filename}, {user}, {upn}.
/// </summary>
public static partial class TextTemplate
{
    public static IReadOnlyList<string> Placeholders { get; } = ["{date}", "{time}", "{filename}", "{user}", "{email}"];

    public static string Expand(string template, string? fileName = null, string? user = null,
                                DateTimeOffset? now = null, string? email = null)
    {
        var t = now ?? DateTimeOffset.Now;
        var inv = CultureInfo.InvariantCulture; // con globalizzazione invariante it-IT non è disponibile

        return PlaceholderRegex().Replace(template, m => m.Groups[1].Value.ToLowerInvariant() switch
        {
            "data" or "date" => t.ToString("dd/MM/yyyy", inv),
            "ora" or "time" => t.ToString("HH:mm", inv),
            "nomefile" or "filename" => fileName is null ? "" : Path.GetFileName(fileName),
            "utente" or "user" => user ?? "",
            "email" or "upn" => email ?? "",
            _ => m.Value,
        }).Trim();
    }

    [GeneratedRegex(@"\{(\w+)\}")]
    private static partial Regex PlaceholderRegex();
}
