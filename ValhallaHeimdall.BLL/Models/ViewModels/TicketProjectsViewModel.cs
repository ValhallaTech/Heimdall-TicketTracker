using System.Collections.Generic;

namespace ValhallaHeimdall.BLL.Models.ViewModels
{
    public class TicketProjectsViewModel
    {
        public Project Project { get; set; }

        public Ticket Ticket { get; set; }

        public List<HeimdallUser> Roles { get; set; }

        public List<Ticket> Tickets { get; set; }
    }
}
