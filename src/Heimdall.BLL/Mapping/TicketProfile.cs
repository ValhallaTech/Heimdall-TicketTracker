using AutoMapper;
using Heimdall.Core.Dtos;
using Heimdall.Core.Models;

namespace Heimdall.BLL.Mapping;

/// <summary>
/// AutoMapper profile mapping between <see cref="Ticket"/> domain entities and
/// <see cref="TicketDto"/> view models.
/// </summary>
public class TicketProfile : Profile
{
    /// <summary>Initializes the mapping profile.</summary>
    public TicketProfile()
    {
        CreateMap<Ticket, TicketDto>();
        CreateMap<TicketDto, Ticket>()
            // DateCreated is set explicitly in CreateAsync (DateTimeOffset.UtcNow) and must be
            // preserved in UpdateAsync — it is never present in a form POST, so mapping it
            // would overwrite the DB timestamp with the DTO default (0001-01-01).
            .ForMember(dest => dest.DateCreated, opt => opt.Ignore())
            // DateUpdated is set explicitly in CreateAsync / UpdateAsync.
            .ForMember(dest => dest.DateUpdated, opt => opt.Ignore());
    }
}
