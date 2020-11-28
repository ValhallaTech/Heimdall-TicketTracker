using System.Threading.Tasks;

namespace ValhallaHeimdall.API.Services
{
    // This is the interface for gathering the ID's to be added to Projects and Tickets
    public interface IHeimdallAccessService
    {
        Task<bool> CanInteractProjectAsync( string userId, int projectId, string roleName );

        Task<bool> CanInteractTicketAsync( string userId, int ticketId, string roleName );
    }
}
