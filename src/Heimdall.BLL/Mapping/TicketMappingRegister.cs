using Heimdall.Core.Dtos;
using Heimdall.Core.Models;
using Mapster;

namespace Heimdall.BLL.Mapping;

/// <summary>
/// Mapster <see cref="IRegister"/> that defines mapping between <see cref="Ticket"/> domain
/// entities and <see cref="TicketDto"/> view models.
/// </summary>
/// <remarks>
/// Mapster's source generator (<c>Mapster.SourceGenerator</c>) reads
/// <see cref="IRegister"/> implementations at compile time and emits the strongly-typed
/// mapper code into <c>obj/</c>, so no expression trees are compiled at runtime.
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
