using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Heimdall.Core.Auditing;
using Heimdall.Core.Models;
using Heimdall.Web.Bootstrap;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using Moq;

namespace Heimdall.Web.Tests.Bootstrap;

public class SystemAdminBootstrapperTests
{
    [Fact]
    public void Should_Throw_When_UserManagerIsNull()
    {
        var harness = new TestHarness();
        Action act = () => new SystemAdminBootstrapper(
            null!, harness.UserStore.Object, harness.AuditWriter.Object, harness.Logger.Object);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Should_Throw_When_UserStoreIsNull()
    {
        var harness = new TestHarness();
        Action act = () => new SystemAdminBootstrapper(
            harness.UserManager.Object, null!, harness.AuditWriter.Object, harness.Logger.Object);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Should_Throw_When_AuditWriterIsNull()
    {
        var harness = new TestHarness();
        Action act = () => new SystemAdminBootstrapper(
            harness.UserManager.Object, harness.UserStore.Object, null!, harness.Logger.Object);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Should_Throw_When_LoggerIsNull()
    {
        var harness = new TestHarness();
        Action act = () => new SystemAdminBootstrapper(
            harness.UserManager.Object, harness.UserStore.Object, harness.AuditWriter.Object, null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public async Task Should_LogSkipped_When_EmailIsNull()
    {
        var harness = new TestHarness();
        await harness.Bootstrapper.RunAsync(null, "ValidPass#9999A", CancellationToken.None);

        harness.Logger.VerifyLog(LogLevel.Information, "skipped");
        harness.UserManager.Verify(m => m.FindByEmailAsync(It.IsAny<string>()), Times.Never);
        harness.AuditWriter.Verify(
            a => a.WriteAsync(It.IsAny<AuditEvent>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Should_LogSkipped_When_PasswordIsNull()
    {
        var harness = new TestHarness();
        await harness.Bootstrapper.RunAsync("ops@example.com", null, CancellationToken.None);

        harness.Logger.VerifyLog(LogLevel.Information, "skipped");
        harness.UserManager.Verify(m => m.FindByEmailAsync(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task Should_LogSkipped_When_BothNull()
    {
        var harness = new TestHarness();
        await harness.Bootstrapper.RunAsync(null, null, CancellationToken.None);

        harness.Logger.VerifyLog(LogLevel.Information, "skipped");
        harness.UserManager.Verify(m => m.FindByEmailAsync(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task Should_LogSkipped_When_EmailWhitespace()
    {
        var harness = new TestHarness();
        await harness.Bootstrapper.RunAsync("   ", "ValidPass#9999A", CancellationToken.None);

        harness.Logger.VerifyLog(LogLevel.Information, "skipped");
        harness.UserManager.Verify(m => m.FindByEmailAsync(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task Should_CreateUser_When_EmailNotFound()
    {
        // Arrange
        var harness = new TestHarness();
        const string email = "ops@example.com";
        const string password = "ValidPass#9999A";
        harness.UserManager
            .Setup(m => m.FindByEmailAsync(email))
            .ReturnsAsync((HeimdallUser?)null);
        HeimdallUser? capturedUser = null;
        harness.UserManager
            .Setup(m => m.CreateAsync(It.IsAny<HeimdallUser>(), password))
            .Callback<HeimdallUser, string>((u, _) => capturedUser = u)
            .ReturnsAsync(IdentityResult.Success);

        // Act
        await harness.Bootstrapper.RunAsync(email, password, CancellationToken.None);

        // Assert
        capturedUser.Should().NotBeNull();
        capturedUser!.Email.Should().Be(email);
        capturedUser.NormalizedEmail.Should().Be(email.ToUpperInvariant());
        capturedUser.EmailConfirmed.Should().BeTrue();
        capturedUser.SystemAdmin.Should().BeTrue();
        capturedUser.Id.Should().NotBe(Guid.Empty);
        capturedUser.SecurityStamp.Should().NotBeNullOrWhiteSpace();
        capturedUser.ConcurrencyStamp.Should().NotBeNullOrWhiteSpace();

        harness.AuditWriter.Verify(
            a => a.WriteAsync(
                It.Is<AuditEvent>(e => e.EventType == "bootstrap.admin.created"),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Should_LogError_When_CreateAsyncFailsDueToPasswordPolicy()
    {
        var harness = new TestHarness();
        const string email = "ops@example.com";
        harness.UserManager
            .Setup(m => m.FindByEmailAsync(email))
            .ReturnsAsync((HeimdallUser?)null);
        var identityError = new IdentityError
        {
            Code = "PasswordTooShort",
            Description = "Passwords must be at least 12 characters.",
        };
        harness.UserManager
            .Setup(m => m.CreateAsync(It.IsAny<HeimdallUser>(), It.IsAny<string>()))
            .ReturnsAsync(IdentityResult.Failed(identityError));

        await harness.Bootstrapper.RunAsync(email, "short", CancellationToken.None);

        harness.Logger.VerifyLog(LogLevel.Error, "PasswordTooShort");
        harness.Logger.VerifyLog(LogLevel.Error, "Passwords must be at least 12 characters.");
        harness.AuditWriter.Verify(
            a => a.WriteAsync(It.IsAny<AuditEvent>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Should_PromoteUser_When_EmailExistsAsNonAdmin()
    {
        var harness = new TestHarness();
        const string email = "ops@example.com";
        var existing = new HeimdallUser
        {
            Id = Guid.NewGuid(),
            Email = email,
            NormalizedEmail = email.ToUpperInvariant(),
            SystemAdmin = false,
        };
        harness.UserManager
            .Setup(m => m.FindByEmailAsync(email))
            .ReturnsAsync(existing);
        HeimdallUser? captured = null;
        harness.UserStore
            .Setup(s => s.UpdateAsync(It.IsAny<HeimdallUser>(), It.IsAny<CancellationToken>()))
            .Callback<HeimdallUser, CancellationToken>((u, _) => captured = u)
            .ReturnsAsync(IdentityResult.Success);

        await harness.Bootstrapper.RunAsync(email, "ValidPass#9999A", CancellationToken.None);

        captured.Should().NotBeNull();
        captured!.SystemAdmin.Should().BeTrue();
        captured.Id.Should().Be(existing.Id);

        harness.UserManager.Verify(m => m.CreateAsync(It.IsAny<HeimdallUser>(), It.IsAny<string>()), Times.Never);
        harness.AuditWriter.Verify(
            a => a.WriteAsync(
                It.Is<AuditEvent>(e => e.EventType == "bootstrap.admin.promoted" && e.ActorUserId == existing.Id),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Should_NoOp_When_EmailExistsAsAdmin()
    {
        var harness = new TestHarness();
        const string email = "ops@example.com";
        var existing = new HeimdallUser
        {
            Id = Guid.NewGuid(),
            Email = email,
            NormalizedEmail = email.ToUpperInvariant(),
            SystemAdmin = true,
        };
        harness.UserManager
            .Setup(m => m.FindByEmailAsync(email))
            .ReturnsAsync(existing);

        await harness.Bootstrapper.RunAsync(email, "ValidPass#9999A", CancellationToken.None);

        harness.UserStore.Verify(
            s => s.UpdateAsync(It.IsAny<HeimdallUser>(), It.IsAny<CancellationToken>()),
            Times.Never);
        harness.UserManager.Verify(
            m => m.CreateAsync(It.IsAny<HeimdallUser>(), It.IsAny<string>()),
            Times.Never);
        harness.AuditWriter.Verify(
            a => a.WriteAsync(It.IsAny<AuditEvent>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Should_LogError_When_PromoteFailsDueToConcurrency()
    {
        var harness = new TestHarness();
        const string email = "ops@example.com";
        var existing = new HeimdallUser
        {
            Id = Guid.NewGuid(),
            Email = email,
            NormalizedEmail = email.ToUpperInvariant(),
            SystemAdmin = false,
        };
        harness.UserManager
            .Setup(m => m.FindByEmailAsync(email))
            .ReturnsAsync(existing);
        var identityError = new IdentityError
        {
            Code = "ConcurrencyFailure",
            Description = "Optimistic concurrency failure, object has been modified.",
        };
        harness.UserStore
            .Setup(s => s.UpdateAsync(It.IsAny<HeimdallUser>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(IdentityResult.Failed(identityError));

        await harness.Bootstrapper.RunAsync(email, "ValidPass#9999A", CancellationToken.None);

        harness.Logger.VerifyLog(LogLevel.Error, "ConcurrencyFailure");
        harness.AuditWriter.Verify(
            a => a.WriteAsync(It.IsAny<AuditEvent>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Should_NotThrow_When_FindByEmailAsyncThrowsUnexpectedException()
    {
        var harness = new TestHarness();
        harness.UserManager
            .Setup(m => m.FindByEmailAsync(It.IsAny<string>()))
            .ThrowsAsync(new InvalidOperationException("transient DB hiccup"));

        Func<Task> act = () => harness.Bootstrapper.RunAsync("ops@example.com", "ValidPass#9999A", CancellationToken.None);

        await act.Should().NotThrowAsync();
        harness.Logger.VerifyLog(LogLevel.Error, "bootstrap failed unexpectedly");
    }

    [Fact]
    public async Task Should_PropagateOperationCanceledException_When_TokenCancelled()
    {
        var harness = new TestHarness();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        harness.UserManager
            .Setup(m => m.FindByEmailAsync(It.IsAny<string>()))
            .ThrowsAsync(new OperationCanceledException(cts.Token));

        Func<Task> act = () => harness.Bootstrapper.RunAsync("ops@example.com", "ValidPass#9999A", cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task Should_OmitEmailAndDomainFromAuditPayload_When_AdminCreated()
    {
        var harness = new TestHarness();
        const string email = "ops@corporate.example.com";
        const string password = "ValidPass#9999A";
        harness.UserManager
            .Setup(m => m.FindByEmailAsync(email))
            .ReturnsAsync((HeimdallUser?)null);
        harness.UserManager
            .Setup(m => m.CreateAsync(It.IsAny<HeimdallUser>(), password))
            .ReturnsAsync(IdentityResult.Success);
        AuditEvent? captured = null;
        harness.AuditWriter
            .Setup(a => a.WriteAsync(It.IsAny<AuditEvent>(), It.IsAny<CancellationToken>()))
            .Callback<AuditEvent, CancellationToken>((e, _) => captured = e)
            .Returns(Task.CompletedTask);

        await harness.Bootstrapper.RunAsync(email, password, CancellationToken.None);

        captured.Should().NotBeNull();
        // Audit payload must contain neither the full email nor its domain — the
        // ActorUserId already identifies the bootstrapped account, and writing any
        // form of the address would constitute a PII sink.
        captured!.PayloadJson.Should().NotContain("email_domain");
        captured.PayloadJson.Should().NotContain("corporate.example.com");
        captured.PayloadJson.Should().NotContain("ops@");
        captured.PayloadJson.Should().NotContain("ops\"");

        // Sanity-check JSON structure
        using JsonDocument doc = JsonDocument.Parse(captured.PayloadJson);
        doc.RootElement.TryGetProperty("email_domain", out _).Should().BeFalse();
        doc.RootElement.TryGetProperty("email", out _).Should().BeFalse();
    }

    // ---------------------------------------------------------------------
    // Test harness
    // ---------------------------------------------------------------------
    private sealed class TestHarness
    {
        public TestHarness()
        {
            UserStore = new Mock<IUserStore<HeimdallUser>>();
            UserManager = new Mock<UserManager<HeimdallUser>>(
                UserStore.Object, null!, null!, null!, null!, null!, null!, null!, null!);
            UserManager.Setup(m => m.NormalizeEmail(It.IsAny<string>()))
                .Returns<string>(s => s?.ToUpperInvariant() ?? string.Empty);
            AuditWriter = new Mock<IAuditEventWriter>();
            AuditWriter
                .Setup(a => a.WriteAsync(It.IsAny<AuditEvent>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);
            Logger = new Mock<ILogger<SystemAdminBootstrapper>>();
            Logger.Setup(l => l.IsEnabled(It.IsAny<LogLevel>())).Returns(true);
            Bootstrapper = new SystemAdminBootstrapper(
                UserManager.Object, UserStore.Object, AuditWriter.Object, Logger.Object);
        }

        public Mock<IUserStore<HeimdallUser>> UserStore { get; }

        public Mock<UserManager<HeimdallUser>> UserManager { get; }

        public Mock<IAuditEventWriter> AuditWriter { get; }

        public Mock<ILogger<SystemAdminBootstrapper>> Logger { get; }

        public SystemAdminBootstrapper Bootstrapper { get; }
    }
}

internal static class LoggerMockExtensions
{
    public static void VerifyLog<T>(this Mock<ILogger<T>> logger, LogLevel level, string substring)
    {
        logger.Verify(
            l => l.Log(
                level,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((state, _) => state.ToString()!.Contains(substring, StringComparison.OrdinalIgnoreCase)),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.AtLeastOnce);
    }
}
