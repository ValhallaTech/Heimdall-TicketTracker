using System;

namespace ValhallaHeimdall.BLL.Models
{
    public class Notification
    {
        public int Id { get; set; }

        public int TicketId { get; set; }

        public string Description { get; set; }

        public DateTimeOffset Created { get; set; }

        public string RecipientId { get; set; }

        public string SenderId { get; set; }

        public bool Viewed { get; set; }

        public string UserId { get; set; }

        public virtual Ticket Ticket { get; set; }

        public virtual HeimdallUser Recipient { get; set; }

        public virtual HeimdallUser Sender { get; set; }
    }
}
