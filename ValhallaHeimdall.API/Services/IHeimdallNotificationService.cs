using System.Threading.Tasks;
using ValhallaHeimdall.BLL.Models;

namespace ValhallaHeimdall.API.Services
{
    public interface IHeimdallNotificationService
    {
        public Task NotifyAsync( string userId, Ticket ticket, TicketHistory change );

        public Task NotifyOfCommentAsync( string userId, Ticket ticket, TicketComment comment );

        public Task NotifyOfAttachmentAsync( string userId, Ticket ticket, TicketAttachment attachment );
    }
}
