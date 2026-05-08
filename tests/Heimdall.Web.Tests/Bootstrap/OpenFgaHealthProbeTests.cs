using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Heimdall.BLL.Authorization.OpenFga;
using Heimdall.Web.Bootstrap;
using Heimdall.Web.Tests.Bootstrap.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OpenFga.Sdk.Client;

namespace Heimdall.Web.Tests.Bootstrap;

/// <summary>
/// Unit tests for <see cref="OpenFgaHealthProbe"/>. The OpenFGA SDK is exercised
/// through a fake <see cref="HttpMessageHandler"/> because its client surface is
/// virtual-but-final and cannot be mocked directly.
/// </summary>
public class OpenFgaHealthProbeTests
{
    private const string StoreId = "01HZX0000000000000000000ST";
    private const string ModelId = "01HZX0000000000000000000MD";

    private const string ReadModelOkBody =
        "{\"authorization_model\":{\"id\":\"" + ModelId + "\",\"schema_version\":\"1.1\"}}";

    private static OpenFgaClient BuildClient(FakeHttpMessageHandler handler) =>
        new(
            new ClientConfiguration
            {
                ApiUrl = "http://openfga:8080",
                StoreId = StoreId,
                AuthorizationModelId = ModelId,
            },
            new HttpClient(handler));

    private static OpenFgaHealthProbe BuildSut(
        OpenFgaClient? client,
        OpenFgaOptions options)
    {
        var services = new ServiceCollection();
        if (client is not null)
        {
            services.AddSingleton(client);
        }

        return new OpenFgaHealthProbe(
            services.BuildServiceProvider(),
            Options.Create(options),
            NullLogger<OpenFgaHealthProbe>.Instance);
    }

    [Fact]
    public async Task RunAsync_Should_Throw_When_AnyConstructorArgumentIsNull()
    {
        var sp = new ServiceCollection().BuildServiceProvider();
        IOptions<OpenFgaOptions> opts = Options.Create(new OpenFgaOptions());

        Action act1 = () => new OpenFgaHealthProbe(null!, opts, NullLogger<OpenFgaHealthProbe>.Instance);
        Action act2 = () => new OpenFgaHealthProbe(sp, null!, NullLogger<OpenFgaHealthProbe>.Instance);
        Action act3 = () => new OpenFgaHealthProbe(sp, opts, null!);

        act1.Should().Throw<ArgumentNullException>();
        act2.Should().Throw<ArgumentNullException>();
        act3.Should().Throw<ArgumentNullException>();
        await Task.CompletedTask;
    }

    [Fact]
    public async Task RunAsync_Should_DoNothing_When_HealthProbeDisabled()
    {
        var handler = new FakeHttpMessageHandler();
        var sut = BuildSut(BuildClient(handler), new OpenFgaOptions { HealthProbeEnabled = false });

        await sut.RunAsync(CancellationToken.None);

        handler.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task RunAsync_Should_Throw_When_EnabledButNoClientRegistered()
    {
        var sut = BuildSut(client: null, new OpenFgaOptions
        {
            HealthProbeEnabled = true,
            ApiUrl = string.Empty,
            StoreId = string.Empty,
            AuthorizationModelId = string.Empty,
        });

        Func<Task> act = () => sut.RunAsync(CancellationToken.None);

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*SDK client is not configured*");
    }

    [Fact]
    public async Task RunAsync_Should_Succeed_When_ReadAuthorizationModelReturns200()
    {
        var handler = new FakeHttpMessageHandler
        {
            Responder = (_, _) => Task.FromResult(FakeHttpMessageHandler.Json(ReadModelOkBody)),
        };
        var sut = BuildSut(BuildClient(handler), new OpenFgaOptions
        {
            HealthProbeEnabled = true,
            ApiUrl = "http://openfga:8080",
            StoreId = StoreId,
            AuthorizationModelId = ModelId,
            HealthProbeTimeout = TimeSpan.FromSeconds(5),
        });

        await sut.RunAsync(CancellationToken.None);

        handler.CallCount.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task RunAsync_Should_WrapInInvalidOperationException_When_SidecarReturnsError()
    {
        var handler = new FakeHttpMessageHandler
        {
            Responder = (_, _) => Task.FromResult(FakeHttpMessageHandler.Json(
                "{\"code\":\"validation_error\",\"message\":\"bad model id\"}",
                HttpStatusCode.BadRequest)),
        };
        var sut = BuildSut(BuildClient(handler), new OpenFgaOptions
        {
            HealthProbeEnabled = true,
            ApiUrl = "http://openfga:8080",
            StoreId = StoreId,
            AuthorizationModelId = ModelId,
            HealthProbeTimeout = TimeSpan.FromSeconds(5),
        });

        Func<Task> act = () => sut.RunAsync(CancellationToken.None);

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*health probe failed*");
    }

    [Fact]
    public async Task RunAsync_Should_ThrowTimeout_When_HandlerStallsBeyondTimeout()
    {
        var handler = new FakeHttpMessageHandler
        {
            Responder = async (_, ct) =>
            {
                await Task.Delay(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
                return FakeHttpMessageHandler.Json(ReadModelOkBody);
            },
        };
        var sut = BuildSut(BuildClient(handler), new OpenFgaOptions
        {
            HealthProbeEnabled = true,
            ApiUrl = "http://openfga:8080",
            StoreId = StoreId,
            AuthorizationModelId = ModelId,
            HealthProbeTimeout = TimeSpan.FromMilliseconds(50),
        });

        Func<Task> act = () => sut.RunAsync(CancellationToken.None);

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*timed out*");
    }
}
