using System;
using Xunit;

namespace Heimdall.Tests.Shared.OpenFga;

/// <summary>
/// xUnit <see cref="FactAttribute"/> that auto-skips when
/// <c>HEIMDALL_OPENFGA_TESTS_ENABLED</c> is set to <c>false</c> (case-insensitive).
/// Used by integration tests that depend on <see cref="OpenFgaTestcontainersFixture"/>
/// so a sandbox without Docker / outbound image pulls can opt out without
/// red-lighting the build. Default is "tests run" — the CI runners must keep
/// running them.
/// </summary>
/// <remarks>
/// See <c>docs/proposals/openfga.md</c> §3 step 12 (Phase 3.7 step 12).
/// </remarks>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
public sealed class OpenFgaIntegrationFactAttribute : FactAttribute
{
    /// <summary>Initializes a new instance.</summary>
    public OpenFgaIntegrationFactAttribute()
    {
        string? value = Environment.GetEnvironmentVariable(
            OpenFgaTestcontainersFixture.EnabledEnvVar);
        if (string.Equals(value, "false", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "0", StringComparison.Ordinal))
        {
            Skip = $"Skipped because {OpenFgaTestcontainersFixture.EnabledEnvVar}={value}.";
        }
    }
}

/// <summary>
/// xUnit <see cref="TheoryAttribute"/> sibling of
/// <see cref="OpenFgaIntegrationFactAttribute"/>. Same skip semantics.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
public sealed class OpenFgaIntegrationTheoryAttribute : TheoryAttribute
{
    /// <summary>Initializes a new instance.</summary>
    public OpenFgaIntegrationTheoryAttribute()
    {
        string? value = Environment.GetEnvironmentVariable(
            OpenFgaTestcontainersFixture.EnabledEnvVar);
        if (string.Equals(value, "false", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "0", StringComparison.Ordinal))
        {
            Skip = $"Skipped because {OpenFgaTestcontainersFixture.EnabledEnvVar}={value}.";
        }
    }
}
