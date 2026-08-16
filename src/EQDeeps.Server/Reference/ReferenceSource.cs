using System.Net;
using System.Reflection;

namespace EQDeeps.Server.Reference;

/// <summary>
/// What one conditional fetch came back with. <see cref="Modified"/> false
/// with <see cref="Failed"/> false means the cached copy is still current —
/// the 304 case, which is the usual one and costs no bytes.
/// </summary>
public sealed record ReferenceFetch(bool Modified, string? Content, string? ETag, bool Failed, string? Error = null)
{
    public static ReferenceFetch NotModified(string? etag) => new(false, null, etag, false);

    public static ReferenceFetch Fetched(string content, string? etag) => new(true, content, etag, false);

    public static ReferenceFetch Failure(string error) => new(false, null, null, true, error);
}

/// <summary>
/// Where reference data is read from. An interface so the store can be tested
/// without a network — every test in this repo runs offline, and a feature
/// that phones a third party must not be the one exception.
/// </summary>
public interface IReferenceSource
{
    /// <summary>Human-readable name of the site, for attribution in the UI.</summary>
    string Name { get; }

    /// <summary>Where a person can go to see the same data.</summary>
    string HomeUrl { get; }

    /// <summary>The page a listed NPC has on the site, for the lookup door.</summary>
    string NpcUrl(int id);

    Task<ReferenceFetch> GetAsync(string path, string? etag, CancellationToken ct);
}

/// <summary>
/// Reads EQLBase's published data files (ADR-020).
///
/// <para><b>What leaves the machine.</b> A GET for a static file whose name is
/// a number, with no query string, no cookie, and no body — nothing about the
/// player, their character, their log or what they were fighting. The site's
/// own <c>robots.txt</c> is <c>Allow: /</c> with no exclusions, the files
/// carry <c>Access-Control-Allow-Origin: *</c>, and they revalidate with an
/// ETag, so the steady state is a 304 and no transfer at all. The user agent
/// says who is calling, because a site owner deserves to know.</para>
///
/// <para><b>What is not done.</b> Nothing is redistributed: the data is
/// fetched by the person who wants to look at it, cached on their own disk,
/// and attributed on screen. EQLBase states no licence, so bundling a copy
/// into the installer would be taking something nobody granted — see ADR-020
/// for the ask that would change that.</para>
/// </summary>
public sealed class EqlBaseSource : IReferenceSource, IDisposable
{
    private const string Root = "https://eqlbase.com";
    private readonly HttpClient _http;

    public EqlBaseSource(HttpClient? http = null)
    {
        _http = http ?? new HttpClient(new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
        })
        {
            Timeout = TimeSpan.FromSeconds(20),
        };

        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0";
        _http.DefaultRequestHeaders.UserAgent.ParseAdd($"EQDeeps/{version}");
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("(+https://github.com/Moonchopper/EQDeeps)");
    }

    public string Name => "EQLBase";

    public string HomeUrl => Root;

    public string NpcUrl(int id) => $"{Root}/npcs/{id}/";

    public async Task<ReferenceFetch> GetAsync(string path, string? etag, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, Root + path);
            if (!string.IsNullOrEmpty(etag))
            {
                request.Headers.TryAddWithoutValidation("If-None-Match", etag);
            }

            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotModified)
            {
                return ReferenceFetch.NotModified(etag);
            }

            if (!response.IsSuccessStatusCode)
            {
                return ReferenceFetch.Failure($"{(int)response.StatusCode} {response.ReasonPhrase}");
            }

            var content = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return ReferenceFetch.Fetched(content, response.Headers.ETag?.Tag);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return ReferenceFetch.Failure("timed out");
        }
        catch (HttpRequestException e)
        {
            return ReferenceFetch.Failure(e.Message);
        }
    }

    public void Dispose() => _http.Dispose();
}
