using System.Threading.Tasks;
using ValhallaHeimdall.BLL.Models;

namespace ValhallaHeimdall.API.Services
{
    public interface IHeimdallHistoryService
    {
        Task AddHistoryAsync( Ticket oldTicket, Ticket newTicket, string userId );
    }
}
