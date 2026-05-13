using System;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using FluentAssertions;
using Heimdall.Core.Tokens;

namespace Heimdall.Core.Tests.Tokens;

/// <summary>
/// Phase 5.1 — type-level whitelist tests for <see cref="PublicJwk"/> plus the
/// <see cref="SigningAlgorithm"/> round-trip and <see cref="TokenOptions"/> defaults.
/// The serialiser must emit only the RFC 7517 / 7518 public fields and the record
/// itself must carry no private-component properties at all so future renames can't
/// silently bypass the whitelist.
/// </summary>
public class PublicJwkTests
{
    private static readonly JsonSerializerOptions WhenWritingNullOptions = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    [Fact]
    public void PublicJwk_FromRsa_serialises_with_only_whitelisted_fields()
    {
        using var rsa = RSA.Create(2048);

        var jwk = PublicJwk.FromRsa(rsa, kid: "kid-rsa", alg: "RS256");
        string json = JsonSerializer.Serialize(jwk, WhenWritingNullOptions);

        using var doc = JsonDocument.Parse(json);
        var propertyNames = doc.RootElement.EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.Ordinal);

        propertyNames.Should().BeEquivalentTo(new[] { "kty", "use", "kid", "alg", "n", "e" });
        propertyNames.Should().NotContain(new[] { "d", "p", "q", "dp", "dq", "qi" });
    }

    [Fact]
    public void PublicJwk_FromEcdsa_serialises_with_only_whitelisted_fields()
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        var jwk = PublicJwk.FromEcdsa(ecdsa, kid: "kid-ec", alg: "ES256");
        string json = JsonSerializer.Serialize(jwk, WhenWritingNullOptions);

        using var doc = JsonDocument.Parse(json);
        var propertyNames = doc.RootElement.EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.Ordinal);

        propertyNames.Should().BeEquivalentTo(new[] { "kty", "use", "kid", "alg", "crv", "x", "y" });
        propertyNames.Should().NotContain("d");
    }

    [Fact]
    public void PublicJwk_record_has_no_private_field_properties_at_all()
    {
        // Reflection tripwire: the record contract explicitly carries no private-component
        // properties so future field additions cannot accidentally leak through serialisation.
        string[] privateNames = new[] { "D", "P", "Q", "DP", "DQ", "QI" };

        var propertyNames = typeof(PublicJwk)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name)
            .ToHashSet(StringComparer.Ordinal);

        propertyNames.Should().NotContain(privateNames);
    }

    [Theory]
    [InlineData(SigningAlgorithm.Rs256, "RS256")]
    [InlineData(SigningAlgorithm.Es256, "ES256")]
    public void SigningAlgorithm_ToJwaName_returns_canonical_name(SigningAlgorithm alg, string expected)
    {
        alg.ToJwaName().Should().Be(expected);
    }

    [Fact]
    public void SigningAlgorithm_round_trip_via_TryParseJwaName_succeeds_for_supported_names()
    {
        SigningAlgorithmExtensions.TryParseJwaName("RS256", out var rs).Should().BeTrue();
        rs.Should().Be(SigningAlgorithm.Rs256);

        SigningAlgorithmExtensions.TryParseJwaName("ES256", out var es).Should().BeTrue();
        es.Should().Be(SigningAlgorithm.Es256);
    }

    [Theory]
    [InlineData("HS256")]
    [InlineData("none")]
    [InlineData("")]
    [InlineData(null)]
    public void SigningAlgorithm_TryParseJwaName_rejects_disallowed_or_null_names(string? name)
    {
        SigningAlgorithmExtensions.TryParseJwaName(name, out _).Should().BeFalse();
    }

    [Fact]
    public void SigningAlgorithm_ToJwaName_throws_for_undefined_enum_value()
    {
        // Defence-in-depth: feeding an out-of-range enum surfaces an ArgumentOutOfRangeException
        // so an accidentally-added member can't silently fall through.
        Action act = () => ((SigningAlgorithm)999).ToJwaName();
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void TokenOptions_defaults_match_phase_5_1_spec()
    {
        var opts = new TokenOptions();

        opts.AccessTokenLifetime.Should().Be(TimeSpan.FromMinutes(15));
        opts.SigningKeyOverlap.Should().Be(TimeSpan.FromMinutes(15));
        opts.SigningKeyValidity.Should().Be(TimeSpan.FromDays(90));
    }
}
