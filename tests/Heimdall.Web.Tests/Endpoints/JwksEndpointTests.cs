using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Heimdall.BLL.Tokens;
using Heimdall.Core.Tokens;
using Heimdall.Web.Endpoints;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Moq;

namespace Heimdall.Web.Tests.Endpoints;

/// <summary>
/// Endpoint contract tests for <see cref="JwksEndpointExtensions.MapJwksEndpoint"/>.
/// The endpoint is exercised against a minimal <see cref="TestServer"/> pipeline so the
/// test exercises real routing, real <see cref="IMemoryCache"/>, and the real
/// response-shaping path without booting the full <c>Heimdall.Web</c> host (which
/// would require a Postgres container). <see cref="ISigningKeyService"/> is mocked
/// with <see cref="MockBehavior.Strict"/>.
/// </summary>
public class JwksEndpointTests : IAsyncLifetime
{
    private readonly Mock<ISigningKeyService> _keys = new(MockBehavior.Strict);
    private IHost? _host;
    private HttpClient? _client;
    private IMemoryCache? _cache;

    public async Task InitializeAsync()
    {
        _host = await new HostBuilder()
            .ConfigureWebHost(webHost =>
            {
                webHost.UseTestServer();
                webHost.ConfigureServices(services =>
                {
                    services.AddRouting();
                    services.AddMemoryCache();
                    services.AddSingleton(_keys.Object);
                });
                webHost.Configure(app =>
                {
                    app.UseRouting();
                    app.UseEndpoints(e => e.MapJwksEndpoint());
                });
            })
            .StartAsync();

        _client = _host.GetTestClient();
        _cache = _host.Services.GetRequiredService<IMemoryCache>();
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        if (_host is not null)
        {
            await _host.StopAsync();
            _host.Dispose();
        }
    }

    [Fact]
    public async Task Get_jwks_returns_200_and_jwk_set_media_type()
    {
        _keys.Setup(k => k.GetTrustedKeysAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeTrustedKeys());

        var response = await _client!.GetAsync("/.well-known/jwks.json");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/jwk-set+json");
        response.Headers.CacheControl!.Public.Should().BeTrue();
        response.Headers.CacheControl!.MaxAge.Should().Be(TimeSpan.FromSeconds(300));
    }

    [Fact]
    public async Task Get_jwks_returns_exactly_the_trusted_keys()
    {
        var trusted = MakeTrustedKeys();
        _keys.Setup(k => k.GetTrustedKeysAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(trusted);

        var response = await _client!.GetAsync("/.well-known/jwks.json");
        string body = await response.Content.ReadAsStringAsync();

        var set = JsonSerializer.Deserialize<JwkSet>(body);
        set.Should().NotBeNull();
        set!.Keys.Should().HaveCount(2);
        set.Keys.Select(k => k.Kid).Should().BeEquivalentTo(trusted.Select(t => t.Kid));
    }

    [Fact]
    public async Task Get_jwks_response_contains_no_private_field_names()
    {
        _keys.Setup(k => k.GetTrustedKeysAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeTrustedKeys());

        var response = await _client!.GetAsync("/.well-known/jwks.json");
        string body = await response.Content.ReadAsStringAsync();

        // Belt: walk every property name in the document and ensure none is a private field.
        using var doc = JsonDocument.Parse(body);
        var privateFieldNames = new HashSet<string>(StringComparer.Ordinal) { "d", "p", "q", "dp", "dq", "qi" };
        foreach (string name in EnumerateAllPropertyNames(doc.RootElement))
        {
            privateFieldNames.Should().NotContain(name);
        }

        // Braces: literal regex against the raw body — a `"d":` field would slip through any
        // future serialiser refactor that bypasses PublicJwk's [JsonPropertyName] attributes.
        Regex.IsMatch(body, "\"d\"\\s*:").Should().BeFalse();
        Regex.IsMatch(body, "\"p\"\\s*:").Should().BeFalse();
        Regex.IsMatch(body, "\"q\"\\s*:").Should().BeFalse();
    }

    [Fact]
    public async Task Get_jwks_is_cached_for_5_minutes_and_invalidates_on_signal()
    {
        _keys.Setup(k => k.GetTrustedKeysAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeTrustedKeys());

        await _client!.GetAsync("/.well-known/jwks.json");
        await _client!.GetAsync("/.well-known/jwks.json");

        _keys.Verify(k => k.GetTrustedKeysAsync(It.IsAny<CancellationToken>()), Times.Once);

        // Invalidate via the same key the production invalidator targets — proves the cache
        // key contract (MemoryCacheJwksCacheInvalidator.CacheKey) is the one the endpoint uses.
        new MemoryCacheJwksCacheInvalidator(_cache!).Invalidate();

        await _client!.GetAsync("/.well-known/jwks.json");
        _keys.Verify(k => k.GetTrustedKeysAsync(It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task Get_jwks_is_anonymous()
    {
        _keys.Setup(k => k.GetTrustedKeysAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeTrustedKeys());

        var response = await _client!.GetAsync("/.well-known/jwks.json");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.WwwAuthenticate.Should().BeEmpty();
    }

    private static IReadOnlyList<SigningKeyRecord> MakeTrustedKeys()
    {
        DateTime now = DateTime.UtcNow;

        using var rsa = RSA.Create(2048);
        string rsaKid = "rsa-" + Guid.NewGuid().ToString("N");
        var rsaJwk = PublicJwk.FromRsa(rsa, rsaKid, "RS256");

        using var ec = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        string ecKid = "ec-" + Guid.NewGuid().ToString("N");
        var ecJwk = PublicJwk.FromEcdsa(ec, ecKid, "ES256");

        return new[]
        {
            new SigningKeyRecord(rsaKid, SigningAlgorithm.Rs256, rsaJwk, now, now + TimeSpan.FromDays(90), null, now),
            new SigningKeyRecord(ecKid, SigningAlgorithm.Es256, ecJwk, now, now + TimeSpan.FromDays(90), null, now),
        };
    }

    private static IEnumerable<string> EnumerateAllPropertyNames(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var p in element.EnumerateObject())
                {
                    yield return p.Name;
                    foreach (var nested in EnumerateAllPropertyNames(p.Value))
                    {
                        yield return nested;
                    }
                }

                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    foreach (var nested in EnumerateAllPropertyNames(item))
                    {
                        yield return nested;
                    }
                }

                break;
        }
    }
}
