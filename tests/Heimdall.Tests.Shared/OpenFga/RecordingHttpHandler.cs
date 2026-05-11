using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Heimdall.Tests.Shared.OpenFga;

/// <summary>
/// <see cref="DelegatingHandler"/> that records every outbound HTTP request
/// (method, request URI, and a buffered copy of the request body) so OpenFGA
/// integration tests can assert on the wire-level shape of the SDK calls —
/// notably the atomic-<c>Write</c> request count and the
/// <c>consistency</c> field carried in the JSON body of
/// <c>POST /stores/{store_id}/check</c>.
/// </summary>
/// <remarks>
/// <para>
/// The body buffer is re-attached to the outgoing request so the SDK call
/// still succeeds. The handler is thread-safe (multiple SDK calls from one
/// client may overlap during a test) — both the recording list and per-path
/// counters use <see cref="ConcurrentBag{T}"/> / <see cref="ConcurrentDictionary{TKey,TValue}"/>.
/// </para>
/// <para>
/// Construct with <c>new HttpClient(new RecordingHttpHandler(new HttpClientHandler()))</c>
/// and pass that client to <see cref="OpenFgaTestcontainersFixture.CreateSdkClient(HttpClient?)"/>.
/// </para>
/// </remarks>
public sealed class RecordingHttpHandler : DelegatingHandler
{
    private readonly ConcurrentBag<RecordedRequest> _requests = new();

    /// <summary>Initializes a new instance wrapping the given inner handler.</summary>
    /// <param name="inner">Underlying handler; required.</param>
    public RecordingHttpHandler(HttpMessageHandler inner)
        : base(inner)
    {
        ArgumentNullException.ThrowIfNull(inner);
    }

    /// <summary>Initializes a new instance wrapping a default <see cref="HttpClientHandler"/>.</summary>
    public RecordingHttpHandler()
        : base(new HttpClientHandler())
    {
    }

    /// <summary>Gets the recorded requests in arrival order (best-effort under concurrency).</summary>
    public IReadOnlyList<RecordedRequest> Requests => _requests.ToArray();

    /// <summary>
    /// Counts recorded requests whose path ends with <paramref name="pathSuffix"/>.
    /// Convenience for assertions like "exactly one POST to <c>/write</c>".
    /// </summary>
    /// <param name="pathSuffix">Suffix to match against <see cref="RecordedRequest.Path"/>.</param>
    public int CountByPathSuffix(string pathSuffix)
    {
        ArgumentNullException.ThrowIfNull(pathSuffix);
        return _requests.Count(r => r.Path.EndsWith(pathSuffix, StringComparison.Ordinal));
    }

    /// <inheritdoc />
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        string body = string.Empty;
        if (request.Content is not null)
        {
            // Buffer the body so we can both record it and let the SDK send it.
            byte[] bytes = await request.Content
                .ReadAsByteArrayAsync(cancellationToken)
                .ConfigureAwait(false);
            body = System.Text.Encoding.UTF8.GetString(bytes);

            // Re-attach a fresh stream-backed content with the original
            // headers so the SDK's outbound payload is unchanged.
            ByteArrayContent replacement = new(bytes);
            foreach (KeyValuePair<string, IEnumerable<string>> header in request.Content.Headers)
            {
                replacement.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            request.Content = replacement;
        }

        string path = request.RequestUri?.AbsolutePath ?? string.Empty;
        _requests.Add(new RecordedRequest(request.Method.Method, path, body));

        return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>
/// A snapshot of one HTTP request observed by <see cref="RecordingHttpHandler"/>.
/// </summary>
/// <param name="Method">HTTP method (e.g. <c>POST</c>).</param>
/// <param name="Path">Absolute path portion of the request URI (e.g. <c>/stores/01HX…/check</c>).</param>
/// <param name="Body">UTF-8 decoded request body, or empty string for a request with no body.</param>
public sealed record RecordedRequest(string Method, string Path, string Body);
