using System.Collections.Generic;

namespace ValhallaHeimdall.BLL.Models.ViewModels
{
    public class PmHomeViewModel
    {
        public PmHomeViewModel( )
        {
            this.Tickets    = new List<Ticket>( );
            this.Developers = new List<HeimdallUser>( );
            this.Count      = new List<int>( );
        }

        // data for everyone
        public int NumTickets { get; set; }

        public int NumCritical { get; set; }

        public int NumUnassigned { get; set; }

        public int NumOpen { get; set; }

        public List<Ticket> Tickets { get; set; }

        // pm data
        public List<HeimdallUser> Developers { get; set; }

        public List<HeimdallUser> UsersOnProject { get; set; }

        public List<int> Count { get; set; }

        // Developer data
        public List<Ticket> TicketsAssignedToDev { get; set; }

        public List<Ticket> TicketsOnDevProjects { get; set; }

        // Submitter data
        public List<Ticket> TicketsCreatedByMe { get; set; }

        // notifications
        public List<Notification> Notifications { get; set; }
    }
}
