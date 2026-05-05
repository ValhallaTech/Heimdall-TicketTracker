using Heimdall.Core.Models;
using Heimdall.DAL.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;

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
    /// <see cref="IUserLockoutStore{TUser}"/>). The concrete store is registered as
    /// scoped, and each interface forwards to that single instance per scope so
    /// <see cref="UserManager{TUser}"/> sees a single coherent store regardless of
    /// which abstraction it resolves through.
    /// </summary>
    /// <param name="services">The service collection to add registrations to.</param>
    /// <returns>The same <paramref name="services"/> instance, for chaining.</returns>
    /// <exception cref="System.ArgumentNullException"><paramref name="services"/> is <c>null</c>.</exception>
    public static IServiceCollection AddHeimdallIdentityStores(this IServiceCollection services)
    {
        System.ArgumentNullException.ThrowIfNull(services);

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

        return services;
    }
}
