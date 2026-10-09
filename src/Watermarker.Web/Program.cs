using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.StaticFiles;
using System.Threading.RateLimiting;
using Watermarker.Core;
using Watermarker.Core.Formats;
using Watermarker.Web;

var builder = WebApplication.CreateBuilder(args);

var portal = builder.Configuration.GetSection("Portal").Get<PortalOptions>() ?? new PortalOptions();
long maxBodyBytes = portal.MaxUploadMb * 1024L * 1024L;

builder.Services.AddSingleton(portal);
builder.Services.AddSingleton(_ => new WatermarkEngine(new EngineSettings
{
    // Nella configurazione un valore null diventa stringa vuota: va trattata come "non impostato".
    LibreOfficePath = NullIfEmpty(portal.LibreOfficePath),
    FfmpegPath = NullIfEmpty(portal.FfmpegPath),
    ConverterUrl = NullIfEmpty(portal.ConverterUrl),
    WorkDirectory = NullIfEmpty(portal.WorkDirectory) ?? Path.Combine(Path.GetTempPath(), "watermarker"),
    MaxParallelConversions = portal.MaxConcurrentJobs,
    ConversionTimeout = TimeSpan.FromSeconds(portal.ConversionTimeoutSeconds),
    VideoTimeout = TimeSpan.FromMinutes(portal.VideoTimeoutMinutes),
}));
builder.Services.AddHostedService<JobCleanup>();

// Limiti di dimensione: Kestrel e parser multipart (sotto IIS va impostato anche maxAllowedContentLength).
builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = maxBodyBytes + 1024 * 1024);
builder.Services.Configure<FormOptions>(o => o.MultipartBodyLengthLimit = maxBodyBytes);

// Le elaborazioni sono pesanti (LibreOffice, FFmpeg): ne eseguiamo poche alla volta, le altre attendono in coda.
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status503ServiceUnavailable;
    o.AddConcurrencyLimiter("processing", c =>
    {
        c.PermitLimit = Math.Max(1, portal.MaxConcurrentJobs);
        c.QueueLimit = Math.Max(0, portal.QueueLimit);
        c.QueueProcessingOrder = QueueProcessingOrder.OldestFirst;
    });
});

var app = builder.Build();

app.Use(async (ctx, next) =>
{
    var h = ctx.Response.Headers;
    h["X-Content-Type-Options"] = "nosniff";
    h["X-Frame-Options"] = "DENY";
    h["Referrer-Policy"] = "no-referrer";
    h["Content-Security-Policy"] = "default-src 'self'; img-src 'self' blob: data:; style-src 'self'; script-src 'self'; frame-ancestors 'none'";
    await next();
});

app.UseDefaultFiles();
app.UseStaticFiles();
app.UseRateLimiter();

// ---------------------------------------------------------------- API

// Controllo di salute per Docker e per il bilanciatore. Con l'autenticazione dovrà restare anonimo.
app.MapGet("/healthz", () => Results.Text("ok"));

app.MapGet("/api/capabilities", (WatermarkEngine engine, PortalOptions options) => Results.Ok(new
{
    native = engine.NativeExtensions,
    pdf = engine.PdfExtensions,
    converterAvailable = engine.Converter is not null,
    maxUploadBytes = maxBodyBytes,
    maxTextLength = options.MaxTextLength,
    maxLines = WatermarkOptions.MaxLines,
    fonts = options.EffectiveFonts,
    placeholders = TextTemplate.Placeholders,
}));

app.MapPost("/api/watermark", async (HttpContext http, WatermarkEngine engine, PortalOptions options,
                                     ILogger<Program> log, CancellationToken ct) =>
{
    if (!http.Request.HasFormContentType)
        return Problem(400, "Invalid request", "Send the file as multipart/form-data.");

    IFormCollection form;
    try
    {
        form = await http.Request.ReadFormAsync(ct);
    }
    catch (Exception ex) when (ex is InvalidDataException or BadHttpRequestException { StatusCode: 413 })
    {
        return Problem(413, "File too large", $"The maximum size is {options.MaxUploadMb} MB.");
    }

    var file = form.Files.GetFile("file");
    if (file is null || file.Length == 0)
        return Problem(400, "No file", "Choose the file to watermark.");

    if (!TryReadOptions(form, options, file.FileName, http.User.Identity?.Name, out var wmOptions, out var mode, out var error))
        return Problem(400, "Invalid settings", error);

    var jobs = Path.Combine(engine.Settings.WorkDirectory, "jobs");
    Directory.CreateDirectory(jobs);
    var id = Guid.NewGuid().ToString("N");
    var input = Path.Combine(jobs, id + ".in");
    var sw = Stopwatch.StartNew();

    try
    {
        await using (var fs = File.Create(input))
            await file.CopyToAsync(fs, ct);

        var format = engine.Detect(input, file.FileName);
        var downloadName = WatermarkEngine.SuggestOutputName(file.FileName, format, mode);
        var output = Path.Combine(jobs, id + ".out" + Path.GetExtension(downloadName));

        var result = await Task.Run(() => engine.Process(input, output, wmOptions, mode, file.FileName), ct);

        log.LogInformation(
            "Watermark applied: {File} ({Size} bytes, {Format}, output {Mode}) from {Client} in {Ms} ms, {Warnings} warnings",
            file.FileName, file.Length, format.Description, mode, http.Connection.RemoteIpAddress, sw.ElapsedMilliseconds,
            result.Warnings.Count);

        http.Response.Headers["X-Watermark-Warnings"] =
            Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(result.Warnings));
        http.Response.Headers["X-Watermark-Format"] = Uri.EscapeDataString(format.Description);

        // Il file viene cancellato appena il download è terminato.
        var stream = new FileStream(output, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete,
                                    81920, FileOptions.DeleteOnClose | FileOptions.Asynchronous);
        return Results.File(stream, ContentType(downloadName), downloadName);
    }
    catch (OperationCanceledException) when (ct.IsCancellationRequested)
    {
        return Results.Empty;
    }
    catch (NotSupportedException ex)
    {
        return Problem(415, "Unsupported format", ex.Message);
    }
    catch (Exception ex) when (ex is InvalidDataException or FormatException or ArgumentException)
    {
        log.LogWarning(ex, "File could not be processed: {File}", file.FileName);
        return Problem(422, "File could not be processed", ex.Message);
    }
    catch (TimeoutException ex)
    {
        log.LogWarning(ex, "Timeout on {File}", file.FileName);
        return Problem(504, "Processing took too long", ex.Message);
    }
    catch (Exception ex)
    {
        log.LogError(ex, "Error processing {File}", file.FileName);
        return Problem(500, "Processing failed",
            "The file may be damaged or password-protected. Details are in the server log.");
    }
    finally
    {
        try { File.Delete(input); } catch { /* ripulito da JobCleanup */ }
    }
})
.DisableAntiforgery()
.RequireRateLimiting("processing");

app.Run();

// ---------------------------------------------------------------- funzioni di supporto

static bool TryReadOptions(IFormCollection form, PortalOptions portal, string fileName, string? user,
                           out WatermarkOptions options, out OutputMode mode, out string error)
{
    options = null!;
    mode = OutputMode.Original;
    error = "";

    // Testo su più righe: a capo normalizzati, righe vuote ignorate.
    var text = form["text"].ToString().Replace("\r\n", "\n").Replace('\r', '\n').Trim();
    if (text.Length == 0) { error = "Enter the watermark text."; return false; }
    if (text.Length > portal.MaxTextLength) { error = $"The text can be at most {portal.MaxTextLength} characters long."; return false; }

    if (text.Split('\n').Count(l => l.Trim().Length > 0) > WatermarkOptions.MaxLines)
    { error = $"The watermark can have at most {WatermarkOptions.MaxLines} lines."; return false; }

    if (!TryNumber(form["angle"], -360, 360, 45, out var angle)) { error = "Invalid angle."; return false; }
    if (!TryNumber(form["size"], 4, 400, 60, out var size)) { error = "Invalid size (4-400 points)."; return false; }
    if (!TryNumber(form["opacity"], 0.05, 1, 0.35, out var opacity)) { error = "Invalid opacity."; return false; }

    RgbaColor color;
    try { color = RgbaColor.Parse(form["color"].ToString() is { Length: > 0 } c ? c : "#808080").WithOpacity(opacity); }
    catch (FormatException ex) { error = ex.Message; return false; }

    var font = form["font"].ToString();
    var fonts = portal.EffectiveFonts;
    if (string.IsNullOrEmpty(font)) font = fonts[0];
    if (!fonts.Contains(font, StringComparer.OrdinalIgnoreCase)) { error = "Font not available."; return false; }

    mode = form["output"].ToString() == "pdf" ? OutputMode.Pdf : OutputMode.Original;

    var expanded = TextTemplate.Expand(text, fileName, user);
    if (expanded.Length == 0) { error = "The watermark text is empty after replacing the placeholders."; return false; }

    options = new WatermarkOptions
    {
        Text = expanded,
        AngleDegrees = angle,
        FontSize = size,
        Color = color,
        Font = font,
        Tile = IsOn(form["tile"]),
        PowerPointOnSlides = IsOn(form["pptxSlides"]),
    };
    return true;
}

static bool TryNumber(string? value, double min, double max, double fallback, out double result)
{
    if (string.IsNullOrWhiteSpace(value)) { result = fallback; return true; }
    return double.TryParse(value.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out result)
           && result >= min && result <= max;
}

static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

static bool IsOn(string? value) => value is "true" or "on" or "1";

static string ContentType(string fileName) =>
    new FileExtensionContentTypeProvider().TryGetContentType(fileName, out var ct) ? ct : "application/octet-stream";

static IResult Problem(int status, string title, string detail) =>
    Results.Problem(statusCode: status, title: title, detail: detail);
