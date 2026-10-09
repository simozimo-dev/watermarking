using System.Net.Http.Headers;

namespace Watermarker.Core.Conversion;

/// <summary>Conversione in PDF: in locale (LibreOffice) o tramite un servizio remoto (Gotenberg).</summary>
public interface IPdfConverter
{
    /// <summary>Descrizione per log e diagnostica (es. percorso di soffice o URL del servizio).</summary>
    string Description { get; }

    /// <param name="sourceExtension">Estensione reale del file: decide il filtro di importazione di LibreOffice.</param>
    void ConvertToPdf(string inputPath, string outputPdfPath, string sourceExtension);
}

/// <summary>
/// Conversione delegata a Gotenberg (https://gotenberg.dev), un servizio HTTP che esegue LibreOffice
/// in un container separato. Così un crash o un file malevolo durante la conversione non tocca il
/// portale, e l'immagine del portale non deve contenere LibreOffice.
/// </summary>
public sealed class GotenbergConverter : IPdfConverter
{
    // Il timeout è gestito per richiesta: un HttpClient condiviso per tutta l'applicazione.
    private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };

    private readonly Uri _endpoint;
    private readonly TimeSpan _timeout;

    public GotenbergConverter(string baseUrl, TimeSpan timeout)
    {
        _endpoint = new Uri(new Uri(baseUrl.TrimEnd('/') + "/"), "forms/libreoffice/convert");
        _timeout = timeout;
    }

    public string Description => $"Gotenberg ({_endpoint.GetLeftPart(UriPartial.Authority)})";

    public void ConvertToPdf(string inputPath, string outputPdfPath, string sourceExtension)
    {
        using var file = File.OpenRead(inputPath);
        using var content = new MultipartFormDataContent();
        var part = new StreamContent(file);
        part.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        content.Add(part, "files", "document" + sourceExtension);

        using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint) { Content = content };
        using var cts = new CancellationTokenSource(_timeout);

        HttpResponseMessage response;
        try
        {
            response = Http.Send(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        }
        catch (OperationCanceledException)
        {
            throw new TimeoutException($"The conversion service did not answer within {_timeout.TotalSeconds:0} seconds.");
        }
        catch (HttpRequestException ex)
        {
            throw new InvalidOperationException($"The conversion service is not reachable ({ex.Message}).", ex);
        }

        using (response)
        {
            using var body = response.Content.ReadAsStream(cts.Token);
            if (!response.IsSuccessStatusCode)
            {
                using var reader = new StreamReader(body);
                var detail = reader.ReadToEnd().Trim();
                if (detail.Length > 300) detail = detail[..300] + "…";
                throw new InvalidOperationException(
                    $"The conversion service returned {(int)response.StatusCode}{(detail.Length > 0 ? ": " + detail : ".")}");
            }

            using var output = File.Create(outputPdfPath);
            body.CopyTo(output);
        }
    }
}
