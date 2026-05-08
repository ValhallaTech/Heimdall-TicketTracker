using System;
using FluentAssertions;
using Heimdall.BLL.Authorization.OpenFga;

namespace Heimdall.BLL.Tests.Authorization.OpenFga;

/// <summary>
/// Unit tests for <see cref="OpenFgaOptions"/> covering the constants and
/// default-value contract documented in
/// <c>docs/proposals/security-and-authorization.md</c> §9.2.
/// </summary>
public class OpenFgaOptionsTests
{
    [Fact]
    public void SectionName_Should_BeAuthorizationOpenFga()
    {
        OpenFgaOptions.SectionName.Should().Be("Authorization:OpenFga");
    }

    [Fact]
    public void Defaults_Should_MatchSpecifiedValues()
    {
        var sut = new OpenFgaOptions();

        sut.ApiUrl.Should().BeEmpty();
        sut.StoreId.Should().BeEmpty();
        sut.AuthorizationModelId.Should().BeEmpty();
        sut.PresharedKey.Should().BeNull();
        sut.CacheTtl.Should().Be(TimeSpan.FromSeconds(2));
        sut.HealthProbeTimeout.Should().Be(TimeSpan.FromSeconds(5));
        sut.HealthProbeEnabled.Should().BeFalse();
    }

    [Fact]
    public void Properties_Should_BeMutable()
    {
        var sut = new OpenFgaOptions
        {
            ApiUrl = "http://fga:8080",
            StoreId = "store",
            AuthorizationModelId = "model",
            PresharedKey = "psk",
            CacheTtl = TimeSpan.FromSeconds(10),
            HealthProbeTimeout = TimeSpan.FromSeconds(15),
            HealthProbeEnabled = true,
        };

        sut.ApiUrl.Should().Be("http://fga:8080");
        sut.StoreId.Should().Be("store");
        sut.AuthorizationModelId.Should().Be("model");
        sut.PresharedKey.Should().Be("psk");
        sut.CacheTtl.Should().Be(TimeSpan.FromSeconds(10));
        sut.HealthProbeTimeout.Should().Be(TimeSpan.FromSeconds(15));
        sut.HealthProbeEnabled.Should().BeTrue();
    }
}
