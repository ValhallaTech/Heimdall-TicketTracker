using System;
using System.ComponentModel.DataAnnotations;

namespace ValhallaHeimdall.Models
{
    public class TicketAttachment
    {
        public int Id { get; set; }

        public string FilePath { get; set; }

        [Required]
        public byte[] FileData { get; set; }

        public string Description { get; set; }

        public DateTimeOffset Created { get; set; }

        public int TicketId { get; set; }

        public virtual Ticket Ticket { get; set; }

        public string UserId { get; set; }

        public virtual HeimdallUser User { get; set; }
    }
}
