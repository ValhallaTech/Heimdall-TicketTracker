namespace Heimdall.BLL.Authorization.OpenFga;

/// <summary>
/// Consistency preference forwarded to the OpenFGA SDK on read calls. Mirrors
/// <see cref="OpenFga.Sdk.Model.ConsistencyPreference"/> (the SDK enum) so callers
/// in <c>Heimdall.BLL</c> do not need to reference the SDK directly per
/// <c>docs/proposals/openfga.md</c> §3 step 6.
/// </summary>
public enum FgaConsistency
{
    /// <summary>
    /// Trade off staleness for latency (the OpenFGA default). Suitable for hot UI
    /// paths where the in-process cache already bounds staleness.
    /// </summary>
    MinimizeLatency = 0,

    /// <summary>
    /// Force a strongly-consistent read. Used after a tuple write when the caller
    /// must observe the write — e.g., the read-after-write step in the assignee-change
    /// flow described in <c>docs/proposals/security-and-authorization.md</c> §9.3.
    /// </summary>
    HigherConsistency = 1,
}
