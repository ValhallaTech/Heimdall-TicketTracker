using Heimdall.Core.Dtos;
using Heimdall.Core.Models;
using Mapster;

namespace Heimdall.BLL.Mapping;

/// <summary>
/// Mapster <see cref="IRegister"/> that defines mapping between <see cref="Ticket"/> domain
/// entities and <see cref="TicketDto"/> view models.
/// </summary>
/// <remarks>
/// These registrations are consumed by the project's <c>Mapster.Tool</c> code-generation
/// workflow to produce the strongly-typed mapper implementation that is committed to source
/// control (see <c>Mappers/TicketMapper.cs</c>), so no expression trees are compiled at
/// runtime.
/// </remarks>
public class TicketMappingRegister : IRegister
{
    /// <inheritdoc />
    public void Register(TypeAdapterConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        config.NewConfig<Ticket, TicketDto>();

        config
            .NewConfig<TicketDto, Ticket>()
            // DateCreated is set explicitly in CreateAsync (DateTimeOffset.UtcNow) and must
            // be preserved in UpdateAsync — it is never present in a form POST, so mapping
            // it would overwrite the DB timestamp with the DTO default (0001-01-01).
            .Ignore(dest => dest.DateCreated)
            // DateUpdated is set explicitly in CreateAsync / UpdateAsync.
            .Ignore(dest => dest.DateUpdated);
    }
}
