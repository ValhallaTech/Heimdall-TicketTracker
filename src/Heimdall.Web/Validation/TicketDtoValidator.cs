using System;
using FluentValidation;
using Heimdall.Core.Dtos;

namespace Heimdall.Web.Validation;

/// <summary>
/// Phase 6.5 server-side validation enforcement boundary for <see cref="TicketDto"/>.
/// This validator is the authoritative backend validation gate for the ticket API
/// (<c>docs/proposals/phase-6-adr.md</c> §5, Option C "Hybrid"), mirroring the
/// frontend Zod schema so the client and server agree on the same rules and messages.
/// It reproduces the exact field keys and messages of the legacy hand-rolled
/// <c>TryValidate</c> check so the 422 (<c>application/problem+json</c>) response
/// contract stays byte-compatible.
/// </summary>
public sealed class TicketDtoValidator : AbstractValidator<TicketDto>
{
    /// <summary>
    /// Initializes a new instance of the <see cref="TicketDtoValidator"/> class,
    /// configuring the rules that mirror the frontend Zod schema.
    /// </summary>
    public TicketDtoValidator()
    {
        RuleFor(x => x.Title)
            .NotEmpty().WithMessage("Title is required.")
            .MaximumLength(200).WithMessage("Title must be 200 characters or fewer.");

        RuleFor(x => x.Description)
            .NotEmpty().WithMessage("Description is required.")
            .MaximumLength(4000).WithMessage("Description must be 4000 characters or fewer.");

        RuleFor(x => x.ProjectId)
            .NotEqual(Guid.Empty).WithMessage("ProjectId is required.");

        RuleFor(x => x.TeamId)
            .NotEqual(Guid.Empty).WithMessage("TeamId is required.");

        RuleFor(x => x.ReporterId)
            .NotEqual(Guid.Empty).WithMessage("ReporterId is required.");
    }
}
