using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Heimdall.BLL.Tests.Authorization.OpenFga.TestSupport;

/// <summary>
/// Minimal <see cref="HttpMessageHandler"/> used to fake the OpenFGA HTTP API
/// without spinning up a real sidecar. The OpenFGA SDK methods used by the
/// production code under test are all virtual-but-final, so the SDK itself
/// cannot be Moq'd; intercepting at the HTTP layer is the supported seam.
/// </summary>
internal sealed class FakeHttpMessageHandler : HttpMessageHandler
{
    /// <summary>Delegate that produces a response (or throws) for a given request.</summary>
    public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? Responder { get; set; }

    /// <summary>Captured requests, in invocation order.</summary>
    public List<CapturedRequest> Requests { get; } = new();

    /// <summary>Number of times <see cref="SendAsync"/> was invoked.</summary>
    public int CallCount { get; private set; }

    public static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        CallCount++;
        string? body = request.Content is null
            ? null
            : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        Requests.Add(new CapturedRequest(request.Method, request.RequestUri!, body));

        if (Responder is null)
        {
            return Json("{}");
        }

        return await Responder(request, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>A captured request snapshot — the original message is recycled by the SDK.</summary>
    public sealed record CapturedRequest(HttpMethod Method, Uri Uri, string? Body);
}
