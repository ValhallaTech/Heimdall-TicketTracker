namespace Heimdall.Web.Identity;

/// <summary>
/// Strongly-typed binding for the <c>Registration</c> configuration section
/// (Phase 1 step 10 of <c>docs/proposals/security-and-authorization.md</c> §9.3).
/// Self-service registration is gated on <em>both</em> <see cref="Enabled"/>
/// being <c>true</c> <em>and</em> the email sender being active — opening
/// registration to the world is a Day-2 operational decision.
/// </summary>
public sealed class RegistrationOptions
{
    /// <summary>
    /// Gets or sets a value indicating whether the self-service registration
    /// flow is enabled. Defaults to <c>false</c>; flip to <c>true</c> in
    /// configuration once SMTP is provisioned and the operator wants public
    /// sign-up. The <c>/register</c> endpoint and page both honour this flag.
    /// </summary>
    public bool Enabled { get; set; }
}
