#nullable enable
using System.Collections.Generic;
using Heimdall.BLL.Mapping;
using Heimdall.Core.Dtos;
using Heimdall.Core.Models;

namespace Heimdall.BLL.Mapping
{
    public partial class TicketMapper : ITicketMapper
    {
        public TicketDto Map(Ticket p1)
        {
            return p1 == null ? null : new TicketDto()
            {
                Id = p1.Id,
                Title = p1.Title,
                Description = p1.Description,
                Status = p1.Status,
                Priority = p1.Priority,
                Reporter = p1.Reporter,
                Assignee = p1.Assignee,
                DateCreated = p1.DateCreated,
                DateUpdated = p1.DateUpdated
            };
        }
        public Ticket Map(TicketDto p2)
        {
            return p2 == null ? null : new Ticket()
            {
                Id = p2.Id,
                Title = p2.Title,
                Description = p2.Description,
                Status = p2.Status,
                Priority = p2.Priority,
                Reporter = p2.Reporter,
                Assignee = p2.Assignee
            };
        }
        public IReadOnlyList<TicketDto> Map(IReadOnlyList<Ticket> p3)
        {
            if (p3 == null)
            {
                return null;
            }
            IReadOnlyList<TicketDto> result = new List<TicketDto>();
            
            ICollection<TicketDto> list = (ICollection<TicketDto>)result;
            
            IEnumerator<Ticket> enumerator = p3.GetEnumerator();
            
            while (enumerator.MoveNext())
            {
                Ticket item = enumerator.Current;
                list.Add(item == null ? null : new TicketDto()
                {
                    Id = item.Id,
                    Title = item.Title,
                    Description = item.Description,
                    Status = item.Status,
                    Priority = item.Priority,
                    Reporter = item.Reporter,
                    Assignee = item.Assignee,
                    DateCreated = item.DateCreated,
                    DateUpdated = item.DateUpdated
                });
            }
            return result;
            
        }
    }
}