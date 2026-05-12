using Heimdall.Core.Models;
using Heimdall.DAL.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Heimdall.DAL.Extensions;

/// <summary>
/// <see cref="IServiceCollection"/> extension methods that wire up the Heimdall
/// ASP.NET Core Identity stores. Registered separately from <c>AddDal</c> so that
/// non-web hosts (migration runners, CLI tools) can opt out of pulling in the
/// Identity abstractions.
/// </summary>
public static class IdentityServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="HeimdallUserStore"/> as the Dapper-backed implementation
    /// of every Identity user-store interface that Heimdall supports
    /// (<see cref="IUserStore{TUser}"/>, <see cref="IUserPasswordStore{TUser}"/>,
    /// <see cref="IUserEmailStore{TUser}"/>, <see cref="IUserSecurityStampStore{TUser}"/>,
    /// <see cref="IUserLockoutStore{TUser}"/>, <see cref="IUserTwoFactorStore{TUser}"/>,
    /// <see cref="IUserAuthenticatorKeyStore{TUser}"/>,
    /// <see cref="IUserTwoFactorRecoveryCodeStore{TUser}"/>). The concrete store is registered as
    /// scoped, and each interface forwards to that single instance per scope so
    /// <see cref="UserManager{TUser}"/> sees a single coherent store regardless of
    /// which abstraction it resolves through.
    /// </summary>
    /// <remarks>
    /// <see cref="HeimdallUserStore"/> depends on <see cref="IPasswordHasher{TUser}"/>
    /// to hash and verify two-factor recovery codes. <c>AddIdentityCore&lt;TUser&gt;()</c>
    /// registers a default <see cref="PasswordHasher{TUser}"/> for the host application,
    /// but non-web hosts that call <see cref="AddHeimdallIdentityStores"/> directly
    /// (migration runners, integration tests, CLI tools) may not invoke
    /// <c>AddIdentityCore</c>. To keep the store activatable in those scenarios we
    /// fall back to <see cref="ServiceCollectionDescriptorExtensions.TryAddSingleton{TService, TImplementation}(IServiceCollection)"/>
    /// — if the host has already registered a hasher (e.g. via <c>AddIdentityCore</c>)
    /// that registration wins; otherwise the default <see cref="PasswordHasher{TUser}"/>
    /// is used.
    /// </remarks>
    /// <param name="services">The service collection to add registrations to.</param>
    /// <returns>The same <paramref name="services"/> instance, for chaining.</returns>
    /// <exception cref="System.ArgumentNullException"><paramref name="services"/> is <c>null</c>.</exception>
    public static IServiceCollection AddHeimdallIdentityStores(this IServiceCollection services)
    {
        System.ArgumentNullException.ThrowIfNull(services);

        // Provide a default IPasswordHasher<HeimdallUser> so the store is activatable
        // even in non-web hosts that do not call AddIdentityCore. TryAdd ensures we
        // never overwrite a hasher the host has already configured.
        services.TryAddSingleton<IPasswordHasher<HeimdallUser>, PasswordHasher<HeimdallUser>>();

        // Register the concrete store once per scope, then forward each Identity
        // interface to the same instance. This mirrors the pattern used by
        // EF Core's UserStore<TUser>, which implements many interfaces on a single
        // type — UserManager<TUser> resolves whichever interface it needs and gets
        // back the same object.
        services.AddScoped<HeimdallUserStore>();
        services.AddScoped<IUserStore<HeimdallUser>>(sp => sp.GetRequiredService<HeimdallUserStore>());
        services.AddScoped<IUserPasswordStore<HeimdallUser>>(sp => sp.GetRequiredService<HeimdallUserStore>());
        services.AddScoped<IUserEmailStore<HeimdallUser>>(sp => sp.GetRequiredService<HeimdallUserStore>());
        services.AddScoped<IUserSecurityStampStore<HeimdallUser>>(sp => sp.GetRequiredService<HeimdallUserStore>());
        services.AddScoped<IUserLockoutStore<HeimdallUser>>(sp => sp.GetRequiredService<HeimdallUserStore>());
        services.AddScoped<IUserTwoFactorStore<HeimdallUser>>(sp => sp.GetRequiredService<HeimdallUserStore>());
        services.AddScoped<IUserAuthenticatorKeyStore<HeimdallUser>>(sp => sp.GetRequiredService<HeimdallUserStore>());
        services.AddScoped<IUserTwoFactorRecoveryCodeStore<HeimdallUser>>(sp => sp.GetRequiredService<HeimdallUserStore>());

        return services;
    }
}
