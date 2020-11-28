using System;

namespace ValhallaHeimdall.Models
{
    public class TicketComment
    {
        public int Id { get; set; }

        public string Comment { get; set; }

        public DateTimeOffset Created { get; set; }

        public int TicketId { get; set; }

        public string UserId { get; set; }

        public virtual Ticket Ticket { get; set; }

        public virtual HeimdallUser User { get; set; }
    }
}
