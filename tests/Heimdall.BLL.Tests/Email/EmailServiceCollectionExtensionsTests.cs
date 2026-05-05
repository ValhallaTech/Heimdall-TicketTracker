using System.Collections.Generic;
using FluentAssertions;
using Heimdall.BLL.Email;
using Heimdall.Core.Email;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Heimdall.BLL.Tests.Email;

public class EmailServiceCollectionExtensionsTests
{
    private static IConfiguration BuildConfig(IDictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    private static Dictionary<string, string?> AllConfigured() => new()
    {
        ["Email:Smtp:Host"] = "smtp.test.local",
        ["Email:Smtp:UserName"] = "user",
        ["Email:Smtp:Password"] = "pass",
        ["Email:Smtp:From"] = "sender@test.local",
    };

    private static ServiceProvider Build(IDictionary<string, string?> config)
    {
        var services = new ServiceCollection();
        services.AddSingleton(NullLoggerFactory.Instance);
        services.AddLogging();
        services.AddHeimdallEmail(BuildConfig(config));
        return services.BuildServiceProvider();
    }

    [Fact]
    public void Should_Throw_When_ServicesIsNull()
    {
        Action act = () => EmailServiceCollectionExtensions.AddHeimdallEmail(null!, BuildConfig(new Dictionary<string, string?>()));
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Should_Throw_When_ConfigurationIsNull()
    {
        var services = new ServiceCollection();
        Action act = () => services.AddHeimdallEmail(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Should_RegisterMailKitSender_When_AllKeysConfigured()
    {
        using var sp = Build(AllConfigured());

        var sender = sp.GetRequiredService<IEmailSender>();
        var info = sp.GetRequiredService<EmailSenderRegistrationInfo>();

        sender.Should().BeOfType<MailKitEmailSender>();
        info.ChosenImplementation.Should().Be(nameof(MailKitEmailSender));
        info.Reason.Should().Be("Email:Smtp Host/UserName/Password/From all configured");
    }

    [Theory]
    [InlineData("Email:Smtp:Host", "Host")]
    [InlineData("Email:Smtp:UserName", "UserName")]
    [InlineData("Email:Smtp:Password", "Password")]
    [InlineData("Email:Smtp:From", "From")]
    public void Should_FallBackToNoOpSender_When_KeyMissing(string keyToClear, string expectedMissing)
    {
        var config = AllConfigured();
        config[keyToClear] = string.Empty;

        using var sp = Build(config);
        var sender = sp.GetRequiredService<IEmailSender>();
        var info = sp.GetRequiredService<EmailSenderRegistrationInfo>();

        sender.Should().BeOfType<NoOpEmailSender>();
        info.ChosenImplementation.Should().Be(nameof(NoOpEmailSender));
        info.Reason.Should().StartWith("Missing one or more of Email:Smtp Host/UserName/Password/From");
        info.Reason.Should().Contain(expectedMissing);
    }

    [Fact]
    public void Should_FallBackToNoOpSender_When_AllKeysMissing()
    {
        using var sp = Build(new Dictionary<string, string?>());

        var sender = sp.GetRequiredService<IEmailSender>();
        var info = sp.GetRequiredService<EmailSenderRegistrationInfo>();

        sender.Should().BeOfType<NoOpEmailSender>();
        info.ChosenImplementation.Should().Be(nameof(NoOpEmailSender));
        info.Reason.Should().Contain("Host");
        info.Reason.Should().Contain("UserName");
        info.Reason.Should().Contain("Password");
        info.Reason.Should().Contain("From");
    }

    [Fact]
    public void Should_NotLeakSecretsInReason_When_KeysConfigured()
    {
        var config = AllConfigured();
        config["Email:Smtp:Password"] = "super-secret-value";
        config["Email:Smtp:Host"] = string.Empty;

        using var sp = Build(config);
        var info = sp.GetRequiredService<EmailSenderRegistrationInfo>();

        info.Reason.Should().NotContain("super-secret-value");
        info.Reason.Should().NotContain("smtp.test.local");
        info.Reason.Should().NotContain("sender@test.local");
    }
}
