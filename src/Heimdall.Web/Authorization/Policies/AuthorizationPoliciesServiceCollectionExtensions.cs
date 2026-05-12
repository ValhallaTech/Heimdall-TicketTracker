using System;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Heimdall.Web.Authorization.Policies;

/// <summary>
/// DI registration helpers for the policy-based authorization stack added in
/// Phase 3.5 (<c>docs/proposals/openfga.md</c> §3 step 9). Registers both
/// <see cref="OpenFgaAuthorizationHandler"/> and
/// <see cref="SystemAdminAuthorizationHandler"/> so every policy in
/// <see cref="AuthorizationPolicies"/> resolves through the same pipeline.
/// </summary>
public static class AuthorizationPoliciesServiceCollectionExtensions
{
    /// <summary>
    /// Registers the Heimdall authorization handlers and ensures
    /// <see cref="Microsoft.AspNetCore.Http.IHttpContextAccessor"/> is available
    /// (the FGA handler reads route values from the current HTTP request).
    /// </summary>
    /// <remarks>
    /// Handlers are registered as <see cref="ServiceLifetime.Scoped"/> so they
    /// can consume scoped dependencies (the FGA adapter, audit writer, user
    /// lookup) without surfacing captive-dependency warnings under the
    /// validate-on-build path. The ASP.NET policy pipeline resolves handlers
    /// from the request's <see cref="IServiceProvider"/>, so scoped is the
    /// intended lifetime.
    /// </remarks>
    /// <param name="services">The service collection to register into.</param>
    /// <returns><paramref name="services"/> for chaining.</returns>
    /// <exception cref="ArgumentNullException">If <paramref name="services"/> is <c>null</c>.</exception>
    public static IServiceCollection AddHeimdallAuthorizationPolicies(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddHttpContextAccessor();

        services.TryAddEnumerable(
            ServiceDescriptor.Scoped<IAuthorizationHandler, OpenFgaAuthorizationHandler>());
        services.TryAddEnumerable(
            ServiceDescriptor.Scoped<IAuthorizationHandler, SystemAdminAuthorizationHandler>());

        // Phase 4.3 step 8 — fail-closed placeholder. Replaced by the real
        // OpenFGA + amr-aware handler in Phase 4.6 step 16.
        services.TryAddEnumerable(
            ServiceDescriptor.Scoped<IAuthorizationHandler, RequireMfaPlaceholderAuthorizationHandler>());

        return services;
    }
}
