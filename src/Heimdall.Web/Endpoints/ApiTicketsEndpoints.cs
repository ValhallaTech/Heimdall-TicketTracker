using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Heimdall.BLL.Authorization.OpenFga;
using Heimdall.Core.Auditing;
using Heimdall.Core.Dtos;
using Heimdall.Core.Interfaces;
using Heimdall.Core.Models.Pagination;
using Heimdall.Web.Authorization.Policies;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Heimdall.Web.Endpoints;

/// <summary>
/// Phase 5.6 (<c>docs/implementation/phase-5-checklist.md</c> steps 14-15) — the first
/// <c>/api/v1/*</c> ticket endpoints. Minimal API, JSON in / JSON out, bearer-only
/// (the cookie surface stays on the Razor pages). Authorisation flows entirely through
/// the existing Phase 3 named policies (<c>CanViewTicket</c>, <c>CanEditTicket</c>,
/// <c>CanAssignTicket</c>) so there is one and only one policy stack in the application.
/// </summary>
/// <remarks>
/// <para>
/// The list endpoint mirrors the Razor <c>Tickets.razor</c> data path exactly: it
/// resolves the allowed ticket ids through
/// <see cref="IOpenFgaAuthorizationService.ListObjectsAsync(FgaListObjectsRequest, CancellationToken)"/>
/// and pages through
/// <see cref="ITicketService.GetPagedByIdsAsync(IReadOnlyList{int}, PagedQuery, CancellationToken)"/>.
/// </para>
/// <para>
/// The assign endpoint composes <c>RequireMfa</c> with <c>CanAssignTicket</c> in the
/// same way the Razor admin surfaces do (Phase 4.6 step 18): both policies must
/// allow.
/// </para>
/// </remarks>
public static class ApiTicketsEndpoints
{
    private const string TicketIdRouteKey = AuthorizationPolicies.TicketIdRouteKey;
    private const int DefaultPageSize = 25;
    private const int MaxPageSize = 100;
    private const string ProblemJsonContentType = "application/problem+json";

    /// <summary>
    /// Maps the Phase 5.6 ticket endpoints onto <paramref name="endpoints"/>.
    /// Mapped from <c>Program.cs</c> immediately after <see cref="ApiAuthEndpoints.MapApiAuthEndpoints"/>.
    /// </summary>
    /// <param name="endpoints">The route builder.</param>
    /// <returns><paramref name="endpoints"/> for fluent chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="endpoints"/> is <c>null</c>.</exception>
    public static IEndpointRouteBuilder MapApiTicketsEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        RouteGroupBuilder group = endpoints
            .MapGroup("/api/v1/tickets")
            .RequireRateLimiting("api-token")
            .WithTags("Tickets");

        group
            .MapGet("/", HandleListAsync)
            .RequireAuthorization(new AuthorizeAttribute
            {
                AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme,
                Policy = AuthorizationPolicies.CanViewTicket,
            })
            .WithName("ApiTicketsList");

        group
            .MapGet("/{" + TicketIdRouteKey + ":int}", HandleGetByIdAsync)
            .RequireAuthorization(new AuthorizeAttribute
            {
                AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme,
                Policy = AuthorizationPolicies.CanViewTicket,
            })
            .WithName("ApiTicketsGetById");

        group
            .MapPost("/", HandleCreateAsync)
            .RequireAuthorization(new AuthorizeAttribute
            {
                AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme,
                Policy = AuthorizationPolicies.CanEditTicket,
            })
            .WithName("ApiTicketsCreate");

        group
            .MapPut("/{" + TicketIdRouteKey + ":int}", HandleUpdateAsync)
            .RequireAuthorization(new AuthorizeAttribute
            {
                AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme,
                Policy = AuthorizationPolicies.CanEditTicket,
            })
            .WithName("ApiTicketsUpdate");

        // Assign requires both RequireMfa and CanAssignTicket — same composition
        // rule the Razor admin pages use (Phase 4.6 step 18). Both policies must
        // allow; either denial fails the request.
        group
            .MapPost("/{" + TicketIdRouteKey + ":int}/assign", HandleAssignAsync)
            .RequireAuthorization(new AuthorizeAttribute
            {
                AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme,
                Policy = AuthorizationPolicies.RequireMfa,
            })
            .RequireAuthorization(new AuthorizeAttribute
            {
                AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme,
                Policy = AuthorizationPolicies.CanAssignTicket,
            })
            .WithName("ApiTicketsAssign");

        return endpoints;
    }

    /// <summary>
    /// Request body for <c>POST /api/v1/tickets/{ticketId}/assign</c>.
    /// </summary>
    /// <param name="AssigneeId">
    /// The user id to assign the ticket to. Self-assign is supported via
    /// <see cref="ITicketService.ClaimTicketAsync"/> — pass the caller's own id.
    /// </param>
    public sealed record AssignTicketRequest(Guid AssigneeId);

    internal static async Task<IResult> HandleListAsync(
        HttpContext httpContext,
        [FromServices] IOpenFgaAuthorizationService fga,
        [FromServices] ITicketService ticketService,
        [FromQuery] int? page,
        [FromQuery] int? pageSize,
        [FromQuery] string? searchText,
        [FromQuery] string? sortField,
        [FromQuery] string? sortDirection,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentNullException.ThrowIfNull(fga);
        ArgumentNullException.ThrowIfNull(ticketService);

        if (!TryGetActorId(httpContext, out Guid actorId))
        {
            return Results.StatusCode(StatusCodes.Status401Unauthorized);
        }

        IReadOnlyList<string> objectRefs;
        try
        {
            objectRefs = await fga
                .ListObjectsAsync(
                    new FgaListObjectsRequest(
                        TupleShapes.UserRef(actorId),
                        "view",
                        TupleShapes.TicketType),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Deny-closed per Phase 3.5 step 10 — the FGA adapter contract is
            // supposed to swallow transport failures and return empty, but if it
            // throws we treat it the same as "no allowed ids".
            ILoggerFactory loggerFactory = httpContext.RequestServices.GetRequiredService<ILoggerFactory>();
            ILogger logger = loggerFactory.CreateLogger("ApiTicketsEndpoints");
            logger.LogWarning(
                ex,
                "OpenFGA ListObjects threw for {ActorId}; returning empty page.",
                actorId);
            objectRefs = Array.Empty<string>();
        }

        List<int> allowedIds = ExtractTicketIds(objectRefs);

        SortDirection direction = ParseSortDirection(sortDirection);
        PagedQuery query = new(
            page: page ?? 1,
            pageSize: pageSize ?? DefaultPageSize,
            searchText: searchText,
            sortField: sortField ?? "DateCreated",
            sortDirection: direction);

        PagedResult<TicketDto> result = await ticketService
            .GetPagedByIdsAsync(allowedIds, query, cancellationToken)
            .ConfigureAwait(false);

        return Results.Json(result, statusCode: StatusCodes.Status200OK);
    }

    internal static async Task<IResult> HandleGetByIdAsync(
        HttpContext httpContext,
        [FromRoute(Name = TicketIdRouteKey)] int ticketId,
        [FromServices] ITicketService ticketService,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentNullException.ThrowIfNull(ticketService);

        TicketDto? dto = await ticketService
            .GetByIdAsync(ticketId, cancellationToken)
            .ConfigureAwait(false);

        if (dto is null)
        {
            return Results.NotFound();
        }

        return Results.Json(dto, statusCode: StatusCodes.Status200OK);
    }

    internal static async Task<IResult> HandleCreateAsync(
        HttpContext httpContext,
        [FromBody] TicketDto dto,
        [FromServices] ITicketService ticketService,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentNullException.ThrowIfNull(ticketService);

        if (dto is null)
        {
            return UnprocessableEntity("Request body is required.");
        }

        if (!TryValidate(dto, out IDictionary<string, string[]> errors))
        {
            return ValidationProblem(errors);
        }

        TicketDto created = await ticketService
            .CreateAsync(dto, cancellationToken)
            .ConfigureAwait(false);

        string location = string.Create(
            CultureInfo.InvariantCulture,
            $"/api/v1/tickets/{created.Id}");
        return Results.Created(location, created);
    }

    internal static async Task<IResult> HandleUpdateAsync(
        HttpContext httpContext,
        [FromRoute(Name = TicketIdRouteKey)] int ticketId,
        [FromBody] TicketDto dto,
        [FromServices] ITicketService ticketService,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentNullException.ThrowIfNull(ticketService);

        if (dto is null)
        {
            return UnprocessableEntity("Request body is required.");
        }

        if (dto.Id != ticketId)
        {
            dto.Id = ticketId;
        }

        if (!TryValidate(dto, out IDictionary<string, string[]> errors))
        {
            return ValidationProblem(errors);
        }

        bool updated = await ticketService
            .UpdateAsync(dto, cancellationToken)
            .ConfigureAwait(false);

        return updated ? Results.NoContent() : Results.NotFound();
    }

    internal static async Task<IResult> HandleAssignAsync(
        HttpContext httpContext,
        [FromRoute(Name = TicketIdRouteKey)] int ticketId,
        [FromBody] AssignTicketRequest? request,
        [FromServices] ITicketService ticketService,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentNullException.ThrowIfNull(ticketService);

        if (request is null || request.AssigneeId == Guid.Empty)
        {
            return ValidationProblem(new Dictionary<string, string[]>
            {
                [nameof(AssignTicketRequest.AssigneeId)] = new[] { "AssigneeId is required and must be a non-empty Guid." },
            });
        }

        if (!TryGetActorId(httpContext, out Guid actorId))
        {
            return Results.StatusCode(StatusCodes.Status401Unauthorized);
        }

        try
        {
            bool applied = await ticketService
                .AssignTicketAsync(actorId, ticketId, request.AssigneeId, cancellationToken)
                .ConfigureAwait(false);

            // applied == false is the idempotent / no-op branch (ticket missing
            // OR assignee already matches). Treat both as 200 OK; the caller
            // gets back the current state regardless. Returning 404 here would
            // race with concurrent deletes which is not the contract.
            return Results.Json(new { applied }, statusCode: StatusCodes.Status200OK);
        }
        catch (UnauthorizedAccessException)
        {
            // The policy gate is supposed to have caught this, but the service
            // re-checks defensively. Surface as 403, not 500.
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        }
    }

    private static List<int> ExtractTicketIds(IReadOnlyList<string> objectRefs)
    {
        string prefix = TupleShapes.TicketType + ":";
        List<int> ids = new(objectRefs.Count);
        foreach (string r in objectRefs)
        {
            ReadOnlySpan<char> span = r.AsSpan();
            if (span.StartsWith(prefix, StringComparison.Ordinal)
                && int.TryParse(
                    span[prefix.Length..],
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out int id))
            {
                ids.Add(id);
            }
        }

        return ids;
    }

    private static SortDirection ParseSortDirection(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return SortDirection.Descending;
        }

        if (Enum.TryParse(raw, ignoreCase: true, out SortDirection parsed))
        {
            return parsed;
        }

        if (string.Equals(raw, "asc", StringComparison.OrdinalIgnoreCase))
        {
            return SortDirection.Ascending;
        }

        if (string.Equals(raw, "desc", StringComparison.OrdinalIgnoreCase))
        {
            return SortDirection.Descending;
        }

        return SortDirection.Descending;
    }

    private static bool TryGetActorId(HttpContext httpContext, out Guid actorId)
    {
        string? rawSub = httpContext.User.FindFirstValue("sub")
            ?? httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier);
        return Guid.TryParse(rawSub, out actorId);
    }

    private static bool TryValidate(TicketDto dto, out IDictionary<string, string[]> errors)
    {
        Dictionary<string, List<string>> collected = new(StringComparer.Ordinal);

        if (string.IsNullOrWhiteSpace(dto.Title))
        {
            AddError(collected, nameof(TicketDto.Title), "Title is required.");
        }
        else if (dto.Title.Length > 200)
        {
            AddError(collected, nameof(TicketDto.Title), "Title must be 200 characters or fewer.");
        }

        if (string.IsNullOrWhiteSpace(dto.Description))
        {
            AddError(collected, nameof(TicketDto.Description), "Description is required.");
        }
        else if (dto.Description.Length > 4000)
        {
            AddError(collected, nameof(TicketDto.Description), "Description must be 4000 characters or fewer.");
        }

        if (dto.ProjectId == Guid.Empty)
        {
            AddError(collected, nameof(TicketDto.ProjectId), "ProjectId is required.");
        }

        if (dto.TeamId == Guid.Empty)
        {
            AddError(collected, nameof(TicketDto.TeamId), "TeamId is required.");
        }

        if (dto.ReporterId == Guid.Empty)
        {
            AddError(collected, nameof(TicketDto.ReporterId), "ReporterId is required.");
        }

        errors = collected.ToDictionary(kv => kv.Key, kv => kv.Value.ToArray(), StringComparer.Ordinal);
        return collected.Count == 0;
    }

    private static void AddError(Dictionary<string, List<string>> bag, string field, string message)
    {
        if (!bag.TryGetValue(field, out List<string>? list))
        {
            list = new List<string>(1);
            bag[field] = list;
        }

        list.Add(message);
    }

    private static IResult ValidationProblem(IDictionary<string, string[]> errors) =>
        Results.Json(
            new HttpValidationProblemDetails(errors)
            {
                Type = "https://datatracker.ietf.org/doc/html/rfc4918#section-11.2",
                Title = "Unprocessable Entity",
                Status = StatusCodes.Status422UnprocessableEntity,
            },
            statusCode: StatusCodes.Status422UnprocessableEntity,
            contentType: ProblemJsonContentType);

    private static IResult UnprocessableEntity(string detail) =>
        Results.Json(
            new ProblemDetails
            {
                Type = "https://datatracker.ietf.org/doc/html/rfc4918#section-11.2",
                Title = "Unprocessable Entity",
                Status = StatusCodes.Status422UnprocessableEntity,
                Detail = detail,
            },
            statusCode: StatusCodes.Status422UnprocessableEntity,
            contentType: ProblemJsonContentType);
}
