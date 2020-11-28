using System;

namespace ValhallaHeimdall.BLL.Models
{
    public class TicketHistory
    {
        public int Id { get; set; }

        public int TicketId { get; set; }

        public string Property { get; set; }

        public string OldValue { get; set; }

        public string NewValue { get; set; }

        public DateTimeOffset Created { get; set; }

        public string UserId { get; set; }

        public virtual Ticket Ticket { get; set; }

        public virtual HeimdallUser User { get; set; }
    }
}
