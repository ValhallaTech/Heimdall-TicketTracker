using System;
using System.Collections.Generic;
using Heimdall.Core.Email;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Heimdall.BLL.Email;

/// <summary>
/// Diagnostic marker exposing which <see cref="IEmailSender"/> implementation
/// was wired up at startup and a brief, secret-free reason. Resolve and log
/// from the host so operators can confirm the chosen transport.
/// </summary>
public sealed class EmailSenderRegistrationInfo
{
    /// <summary>Gets the simple name of the chosen <see cref="IEmailSender"/> implementation.</summary>
    public string ChosenImplementation { get; init; } = string.Empty;

    /// <summary>
    /// Gets a one-line explanation of the choice. Mentions only WHICH configuration
    /// keys are present or missing — never their values.
    /// </summary>
    public string Reason { get; init; } = string.Empty;
}

/// <summary>
/// DI registration helpers for the email seam. Decides between
/// <see cref="MailKitEmailSender"/> and <see cref="NoOpEmailSender"/> based on
/// whether the <c>Email:Smtp</c> Host, UserName, Password, and From keys are
/// all configured.
/// </summary>
public static class EmailServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="IEmailSender"/> and binds <see cref="SmtpOptions"/>
    /// to the <c>Email:Smtp</c> configuration section. When Host, UserName,
    /// Password, and From are all non-empty, <see cref="MailKitEmailSender"/>
    /// is registered; otherwise the <see cref="NoOpEmailSender"/> fallback is
    /// used. A singleton <see cref="EmailSenderRegistrationInfo"/> records the
    /// choice (with no secrets) for the host to log at startup.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">The application configuration.</param>
    /// <returns>The service collection, for chaining.</returns>
    public static IServiceCollection AddHeimdallEmail(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<SmtpOptions>().Bind(configuration.GetSection("Email:Smtp"));

        var snapshot = configuration.GetSection("Email:Smtp").Get<SmtpOptions>() ?? new SmtpOptions();

        var missing = new List<string>(capacity: 4);
        if (string.IsNullOrWhiteSpace(snapshot.Host))
        {
            missing.Add("Host");
        }

        if (string.IsNullOrWhiteSpace(snapshot.UserName))
        {
            missing.Add("UserName");
        }

        if (string.IsNullOrWhiteSpace(snapshot.Password))
        {
            missing.Add("Password");
        }

        if (string.IsNullOrWhiteSpace(snapshot.From))
        {
            missing.Add("From");
        }

        EmailSenderRegistrationInfo info;
        if (missing.Count == 0)
        {
            services.AddSingleton<IEmailSender, MailKitEmailSender>();
            info = new EmailSenderRegistrationInfo
            {
                ChosenImplementation = nameof(MailKitEmailSender),
                Reason = "Email:Smtp Host/UserName/Password/From all configured",
            };
        }
        else
        {
            services.AddSingleton<IEmailSender, NoOpEmailSender>();
            info = new EmailSenderRegistrationInfo
            {
                ChosenImplementation = nameof(NoOpEmailSender),
                Reason =
                    "Missing one or more of Email:Smtp Host/UserName/Password/From: "
                    + string.Join(", ", missing),
            };
        }

        services.AddSingleton(info);
        return services;
    }
}
