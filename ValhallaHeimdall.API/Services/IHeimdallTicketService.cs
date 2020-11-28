using System.Collections.Generic;
using System.Threading.Tasks;
using ValhallaHeimdall.BLL.Models;

namespace ValhallaHeimdall.API.Services
{
    public interface IHeimdallTicketService
    {
        public Task AssignDeveloperTicketAsync( string userId, int ticketId );

        public List<Ticket> ListDeveloperTickets( string userId );

        public List<Ticket> ListProjectManagerTickets( string userId );

        public List<Ticket> ListSubmitterTickets( string userId );

        public Task<List<Ticket>> ListProjectTicketsAsync( string userId );
    }
}
