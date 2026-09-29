using System.Net;
using System.Net.Http.Headers;

namespace BonsaiSetup.Distribution;

internal sealed class ManifestClient : IDisposable
{
    private readonly HttpClient _http = new(new HttpClientHandler { AllowAutoRedirect = true })
    {
        Timeout = TimeSpan.FromSeconds(45)
    };

    public async Task<DistributionManifest> FetchAsync(string manifestUrl, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(manifestUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(uri.UserInfo))
            throw new InvalidDataException("GitHub manifest URL 未設定或不是 HTTPS。");

        var json = await GetTextAsync(uri, "GitHub stable manifest", cancellationToken).ConfigureAwait(false);
        using var document = System.Text.Json.JsonDocument.Parse(json);
        var profilePath = document.RootElement.GetProperty("hardware_profiles_url").GetString();
        if (string.IsNullOrWhiteSpace(profilePath)) throw new InvalidDataException("GitHub manifest 缺少 hardware_profiles_url。");
        var profileUri = new Uri(uri, profilePath);
        if (profileUri.Scheme != Uri.UriSchemeHttps) throw new InvalidDataException("GitHub hardware profile URL 必須使用 HTTPS。");
        var profileJson = await GetTextAsync(profileUri, "GitHub hardware profiles", cancellationToken).ConfigureAwait(false);
        return DistributionManifest.Parse(json, profileJson);
    }

    private async Task<string> GetTextAsync(Uri uri, string label, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true, NoStore = true };
        request.Headers.UserAgent.ParseAdd("BonsaiLocalDistribution/1.0");
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound) throw new HttpRequestException($"{label} 找不到；請確認 repo URL、分支與相對路徑。");
        response.EnsureSuccessStatusCode();
        if (response.RequestMessage?.RequestUri?.Scheme != Uri.UriSchemeHttps) throw new HttpRequestException($"{label} redirected to a non-HTTPS URL.");
        return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
    }

    public void Dispose() => _http.Dispose();
}
