using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace ValhallaHeimdall.BLL.Models
{
    public class Ticket
    {
        public Ticket( )
        {
            this.Comments      = new HashSet<TicketComment>( );
            this.Attachments   = new HashSet<TicketAttachment>( );
            this.Notifications = new HashSet<Notification>( );
            this.Histories     = new HashSet<TicketHistory>( );
        }

        public int Id { get; set; }

        [Required]
        [StringLength( 50 )]
        public string Title { get; set; }

        [Required]
        public string Description { get; set; }

        [DataType( DataType.Date )]
        public DateTimeOffset Created { get; set; }

        [DataType( DataType.Date )]
        public DateTimeOffset? Updated { get; set; }

        public int ProjectId { get; set; }

        public int TicketTypeId { get; set; }

        public int TicketPriorityId { get; set; }

        [Display( Name = "Status" )]
        public int TicketStatusId { get; set; }

        public string OwnerUserId { get; set; }

        public string DeveloperUserId { get; set; }

        public Project Project { get; set; }

        public TicketType TicketType { get; set; }

        public TicketPriority TicketPriority { get; set; }

        public TicketStatus TicketStatus { get; set; }

        public HeimdallUser OwnerUser { get; set; }

        public HeimdallUser DeveloperUser { get; set; }

        public virtual ICollection<TicketComment> Comments { get; set; }

        public virtual ICollection<TicketAttachment> Attachments { get; set; }

        public virtual ICollection<Notification> Notifications { get; set; }

        public virtual ICollection<TicketHistory> Histories { get; set; }
    }
}
