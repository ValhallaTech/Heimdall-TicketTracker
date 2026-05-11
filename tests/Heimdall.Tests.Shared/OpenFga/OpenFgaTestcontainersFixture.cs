using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Configurations;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Networks;
using OpenFga.Sdk.Client;
using OpenFga.Sdk.Client.Model;
using OpenFga.Sdk.Configuration;
using Testcontainers.PostgreSql;
using Xunit;

namespace Heimdall.Tests.Shared.OpenFga;

/// <summary>
/// Testcontainers fixture for the Heimdall OpenFGA integration tests
/// (<c>docs/proposals/openfga.md</c> §3 step 12 — Phase 3.7 step 12 of the
/// <c>docs/implementation/phase-3-checklist.md</c>).
/// </summary>
/// <remarks>
/// <para>
/// Boots a self-contained OpenFGA sidecar stack:
/// </para>
/// <list type="number">
///   <item><description>A dedicated Postgres container (separate from any app DB).</description></item>
///   <item><description>A one-shot <c>openfga migrate</c> container that materialises the schema.</description></item>
///   <item><description>The long-running <c>openfga/openfga</c> server (HTTP + gRPC) wired to that Postgres.</description></item>
///   <item><description>A one-shot <c>openfga/cli</c> container that transforms <c>authz/model.fga</c> (DSL) into JSON; we then call <see cref="OpenFgaClient.CreateStore"/> + <see cref="OpenFgaClient.WriteAuthorizationModel"/> from .NET to materialise the store.</description></item>
/// </list>
/// <para>
/// The choice to invoke <c>fga model transform</c> via a one-shot CLI container
/// (rather than ship a parsed <c>model.json</c> alongside the DSL) keeps the DSL
/// in <c>authz/model.fga</c> as the single source of truth and avoids a
/// hand-maintained JSON sibling. The transform happens once per fixture instance.
/// </para>
/// <para>
/// Image tags use the floating <c>latest</c> tag per the orchestrator brief
/// (Renovate manages pinning at the repository policy level — pinning here would
/// fight that policy). If a runner is offline / image-restricted, set
/// <c>HEIMDALL_OPENFGA_TESTS_ENABLED=false</c> to skip the fixture-dependent tests.
/// </para>
/// </remarks>
public sealed class OpenFgaTestcontainersFixture : IAsyncLifetime
{
    /// <summary>
    /// Environment variable consulted by <see cref="OpenFgaIntegrationFactAttribute"/>
    /// — when set to <c>false</c> (case-insensitive) test methods that depend on
    /// this fixture are skipped at discovery time so a sandbox without Docker
    /// / outbound image pulls does not red-light the build.
    /// </summary>
    public const string EnabledEnvVar = "HEIMDALL_OPENFGA_TESTS_ENABLED";

    private const string OpenFgaImage = "docker.io/openfga/openfga:latest";
    private const string OpenFgaCliImage = "docker.io/openfga/cli:latest";

    private const string PostgresHostnameAlias = "fga-postgres";
    private const string OpenFgaHostnameAlias = "openfga";
    private const string PostgresUser = "openfga";
    private const string PostgresPassword = "openfga_test";
    private const string PostgresDatabase = "openfga";

    private readonly string _presharedKey = Guid.NewGuid().ToString("N");
    private readonly INetwork _network;
    private readonly PostgreSqlContainer _postgres;

    private IContainer? _openFgaServer;
    private string? _modelJson;
    private string _storeId = string.Empty;
    private string _authorizationModelId = string.Empty;
    private string _apiUrl = string.Empty;

    /// <summary>Initializes a new instance.</summary>
    public OpenFgaTestcontainersFixture()
    {
        _network = new NetworkBuilder()
            .WithName($"heimdall-openfga-{Guid.NewGuid():N}")
            .Build();

        _postgres = new PostgreSqlBuilder("postgres:18-alpine")
            .WithDatabase(PostgresDatabase)
            .WithUsername(PostgresUser)
            .WithPassword(PostgresPassword)
            .WithNetwork(_network)
            .WithNetworkAliases(PostgresHostnameAlias)
            .Build();
    }

    /// <summary>Gets the OpenFGA HTTP API base URL (e.g. <c>http://localhost:54321</c>).</summary>
    public string ApiUrl => _apiUrl;

    /// <summary>Gets the OpenFGA store id created by <see cref="InitializeAsync"/>.</summary>
    public string StoreId => _storeId;

    /// <summary>Gets the pinned authorization-model id produced by writing <c>authz/model.fga</c>.</summary>
    public string AuthorizationModelId => _authorizationModelId;

    /// <summary>Gets the pre-shared API token configured on the sidecar.</summary>
    public string PresharedKey => _presharedKey;

    /// <summary>
    /// Creates a fully-configured <see cref="OpenFgaClient"/> bound to the
    /// fixture's store and pinned model. Each test gets its own client; the
    /// underlying <see cref="HttpClient"/> is owned by the SDK instance.
    /// </summary>
    public OpenFgaClient CreateSdkClient() => CreateSdkClient(httpClient: null);

    /// <summary>
    /// Creates a fully-configured <see cref="OpenFgaClient"/> bound to the
    /// fixture's store and pinned model, using a caller-supplied
    /// <see cref="HttpClient"/>. Tests use this overload to wire a
    /// <see cref="DelegatingHandler"/> that records or counts the requests
    /// the SDK issues to the sidecar (see the Phase 3.7 OpenFGA-Expert
    /// review — atomic-<c>Write</c> request-count proof and
    /// <c>consistency</c>-on-the-wire proof).
    /// </summary>
    /// <param name="httpClient">
    /// Optional pre-built <see cref="HttpClient"/>. When <see langword="null"/>
    /// a fresh default client is allocated. The caller owns disposal of the
    /// instance they pass in.
    /// </param>
    public OpenFgaClient CreateSdkClient(HttpClient? httpClient)
    {
        ClientConfiguration config = new()
        {
            ApiUrl = _apiUrl,
            StoreId = _storeId,
            AuthorizationModelId = _authorizationModelId,
            Credentials = new Credentials
            {
                Method = CredentialsMethod.ApiToken,
                Config = new CredentialsConfig { ApiToken = _presharedKey },
            },
        };
        return new OpenFgaClient(config, httpClient ?? new HttpClient());
    }

    /// <inheritdoc />
    public async Task InitializeAsync()
    {
        // 1. Network + Postgres.
        await _network.CreateAsync().ConfigureAwait(false);
        await _postgres.StartAsync().ConfigureAwait(false);

        string datastoreUri = string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"postgres://{PostgresUser}:{PostgresPassword}@{PostgresHostnameAlias}:5432/{PostgresDatabase}?sslmode=disable");

        // 2. One-shot openfga migrate — runs via docker CLI rather than
        //    Testcontainers' StartAsync because Testcontainers .NET treats a
        //    cleanly-exited container as a startup failure even when the
        //    log-marker wait strategy has matched.
        await RunDockerOneShotAsync(
            "run", "--rm",
            "--network", _network.Name,
            OpenFgaImage,
            "migrate", "--datastore-engine", "postgres",
            "--datastore-uri", datastoreUri).ConfigureAwait(false);

        // 3. Long-running OpenFGA server.
        _openFgaServer = new ContainerBuilder(OpenFgaImage)
            .WithNetwork(_network)
            .WithNetworkAliases(OpenFgaHostnameAlias)
            .WithEnvironment("OPENFGA_DATASTORE_ENGINE", "postgres")
            .WithEnvironment("OPENFGA_DATASTORE_URI", datastoreUri)
            .WithEnvironment("OPENFGA_HTTP_ADDR", "0.0.0.0:8080")
            .WithEnvironment("OPENFGA_GRPC_ADDR", "0.0.0.0:8081")
            .WithEnvironment("OPENFGA_PLAYGROUND_ENABLED", "false")
            .WithEnvironment("OPENFGA_AUTHN_METHOD", "preshared")
            .WithEnvironment("OPENFGA_AUTHN_PRESHARED_KEYS", _presharedKey)
            .WithEnvironment("OPENFGA_LOG_LEVEL", "warn")
            .WithPortBinding(8080, true)
            .WithCommand("run")
            .WithWaitStrategy(
                Wait.ForUnixContainer()
                    .UntilHttpRequestIsSucceeded(req => req
                        .ForPort(8080)
                        .ForPath("/healthz")
                        .ForStatusCode(System.Net.HttpStatusCode.OK)))
            .Build();

        await _openFgaServer.StartAsync().ConfigureAwait(false);

        ushort mappedPort = _openFgaServer.GetMappedPublicPort(8080);
        _apiUrl = string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"http://{_openFgaServer.Hostname}:{mappedPort}");

        // 4. Transform the model DSL → JSON via a one-shot `openfga/cli`
        //    container with the authz dir mounted read-only.
        _modelJson = await TransformDslToJsonAsync().ConfigureAwait(false);

        // 5. Create the store + write the model via the SDK.
        await CreateStoreAndWriteModelAsync().ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task DisposeAsync()
    {
        if (_openFgaServer is not null)
        {
            await _openFgaServer.DisposeAsync().ConfigureAwait(false);
        }

        await _postgres.DisposeAsync().ConfigureAwait(false);
        await _network.DisposeAsync().ConfigureAwait(false);
    }

    private async Task<string> TransformDslToJsonAsync()
    {
        string modelPath = LocateAuthzModelFile();
        string authzDir = Path.GetDirectoryName(modelPath)!;

        // `fga model transform` is offline (no server required), so we run it
        // via docker CLI as a one-shot and capture stdout — Testcontainers
        // would race the container's clean exit against its readiness wait.
        // The image is distroless with `/fga` as its sole entrypoint, so we
        // pass the subcommand args directly (no `--entrypoint` override).
        (int exit, string stdout, string stderr) = await RunDockerCaptureAsync(
            "run", "--rm",
            "-v", $"{authzDir}:/authz:ro",
            OpenFgaCliImage,
            "model", "transform", "--file", "/authz/model.fga").ConfigureAwait(false);

        if (exit != 0 || string.IsNullOrWhiteSpace(stdout))
        {
            throw new InvalidOperationException(
                $"`fga model transform` failed (exit={exit}). stderr={stderr}");
        }

        return stdout;
    }

    private static async Task RunDockerOneShotAsync(params string[] args)
    {
        (int exit, string stdout, string stderr) =
            await RunDockerCaptureAsync(args).ConfigureAwait(false);
        if (exit != 0)
        {
            throw new InvalidOperationException(
                $"docker {string.Join(' ', args)} failed (exit={exit}).\nstdout={stdout}\nstderr={stderr}");
        }
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunDockerCaptureAsync(
        params string[] args)
    {
        ProcessStartInfo psi = new()
        {
            FileName = "docker",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (string a in args)
        {
            psi.ArgumentList.Add(a);
        }

        using Process proc = new() { StartInfo = psi };
        StringBuilder stdout = new();
        StringBuilder stderr = new();
        proc.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                stdout.AppendLine(e.Data);
            }
        };
        proc.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                stderr.AppendLine(e.Data);
            }
        };

        proc.Start();
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();
        await proc.WaitForExitAsync().ConfigureAwait(false);
        return (proc.ExitCode, stdout.ToString(), stderr.ToString());
    }

    private async Task CreateStoreAndWriteModelAsync()
    {
        // Use a bootstrap client with only ApiUrl + Credentials configured.
        ClientConfiguration bootstrapConfig = new()
        {
            ApiUrl = _apiUrl,
            Credentials = new Credentials
            {
                Method = CredentialsMethod.ApiToken,
                Config = new CredentialsConfig { ApiToken = _presharedKey },
            },
        };
        using HttpClient httpClient = new();
        OpenFgaClient bootstrap = new(bootstrapConfig, httpClient);

        ClientCreateStoreRequest createReq = new() { Name = "heimdall-tests" };
        var createResp = await bootstrap.CreateStore(createReq).ConfigureAwait(false);
        _storeId = createResp.Id;

        // Pin the freshly-created store on the bootstrap client so the next call lands.
        bootstrap.StoreId = _storeId;

        var writeModelReq = ClientWriteAuthorizationModelRequest.FromJson(_modelJson!);
        var writeResp = await bootstrap.WriteAuthorizationModel(writeModelReq).ConfigureAwait(false);
        _authorizationModelId = writeResp.AuthorizationModelId;
    }

    private static string LocateAuthzModelFile()
    {
        // Walk up from the test assembly until we find the repo's `authz/model.fga`.
        string? dir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
        while (!string.IsNullOrEmpty(dir))
        {
            string candidate = Path.Combine(dir, "authz", "model.fga");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            dir = Path.GetDirectoryName(dir);
        }

        throw new FileNotFoundException(
            "Unable to locate authz/model.fga by walking up from the test assembly directory.");
    }
}
