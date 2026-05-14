using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Heimdall.BLL.Tokens;
using Heimdall.Core.Tokens;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Caching.Memory;

namespace Heimdall.Web.Endpoints;

/// <summary>
/// Phase 5.1 step 3 — public JWKS endpoint at <c>/.well-known/jwks.json</c>
/// (<see href="https://datatracker.ietf.org/doc/html/rfc7517">RFC 7517 §5</see>).
/// </summary>
/// <remarks>
/// <para>
/// Publishes only the public halves of the trusted signing keys, serialised through the
/// strongly-typed <see cref="PublicJwk"/> whitelist — so the RSA private components
/// (<c>d</c>, <c>p</c>, <c>q</c>, <c>dp</c>, <c>dq</c>, <c>qi</c>) and the EC private
/// scalar (<c>d</c>) cannot leak through the JSON surface, by construction of the type.
/// </para>
/// <para>
/// The serialised body is cached in <see cref="IMemoryCache"/> under the key
/// <see cref="MemoryCacheJwksCacheInvalidator.CacheKey"/> for 5 minutes; rotation via
/// <c>SigningKeyService.GenerateAsync</c> / <c>RetireAsync</c> evicts the entry through
/// <see cref="IJwksCacheInvalidator"/> so external verifiers see the new key on the next
/// request rather than waiting for the TTL to expire (hardening §2.3b).
/// </para>
/// </remarks>
public static class JwksEndpointExtensions
{
    /// <summary>
    /// IANA media type for a JWK Set serialised as JSON
    /// (<see href="https://datatracker.ietf.org/doc/html/rfc7517#section-8.5.1">RFC 7517 §8.5.1</see>).
    /// </summary>
    public const string JwkSetMediaType = "application/jwk-set+json";

    private const int CacheTtlSeconds = 300;

    private static readonly JsonSerializerOptions JwkSetJsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,

        // PropertyNamingPolicy is intentionally null; PublicJwk / JwkSet carry explicit
        // [JsonPropertyName] attributes that match the RFC 7517 / 7518 lowercase names.
        PropertyNamingPolicy = null,
    };

    /// <summary>
    /// Maps <c>GET /.well-known/jwks.json</c> onto <paramref name="endpoints"/>.
    /// Anonymous; cache headers set to <c>Cache-Control: public, max-age=300</c> with
    /// the <see cref="JwkSetMediaType"/> content type.
    /// </summary>
    /// <param name="endpoints">The route builder.</param>
    /// <returns>The same <paramref name="endpoints"/> instance, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="endpoints"/> is <c>null</c>.</exception>
    public static IEndpointRouteBuilder MapJwksEndpoint(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapGet("/.well-known/jwks.json", HandleAsync)
            .AllowAnonymous()
            .WithName("Jwks");

        return endpoints;
    }

    private static async Task<IResult> HandleAsync(
        HttpContext context,
        ISigningKeyService keys,
        IMemoryCache cache,
        CancellationToken cancellationToken)
    {
        string json = await cache.GetOrCreateAsync(
            MemoryCacheJwksCacheInvalidator.CacheKey,
            async entry =>
            {
                entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(CacheTtlSeconds);
                IReadOnlyList<SigningKeyRecord> trusted = await keys
                    .GetTrustedKeysAsync(cancellationToken)
                    .ConfigureAwait(false);

                List<PublicJwk> jwks = new(trusted.Count);
                foreach (SigningKeyRecord r in trusted)
                {
                    jwks.Add(r.PublicJwk);
                }

                var set = new JwkSet { Keys = jwks };
                return JsonSerializer.Serialize(set, JwkSetJsonOptions);
            }).ConfigureAwait(false) ?? "{\"keys\":[]}";

        // Cache-Control bounds external verifier staleness; the in-process invalidation
        // in SigningKeyService.GenerateAsync / RetireAsync still propagates rotation
        // immediately to *this* replica, so the TTL is a conservative upper bound
        // (security-and-authorization §5.1 / hardening §2.3).
        context.Response.Headers.CacheControl = $"public, max-age={CacheTtlSeconds}";
        return Results.Content(json, JwkSetMediaType);
    }
}
