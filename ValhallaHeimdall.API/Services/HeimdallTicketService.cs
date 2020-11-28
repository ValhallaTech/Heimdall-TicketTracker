using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using ValhallaHeimdall.BLL.Models;
using ValhallaHeimdall.DAL.Data;

namespace ValhallaHeimdall.API.Services
{
    public class HeimdallTicketService : IHeimdallTicketService
    {
        private readonly ApplicationDbContext context;

        private readonly UserManager<HeimdallUser> userManager;

        private readonly IHeimdallProjectService projectService;

        public HeimdallTicketService(
            ApplicationDbContext      context,
            UserManager<HeimdallUser> userManager,
            IHeimdallProjectService   projectService )
        {
            this.context        = context;
            this.userManager    = userManager;
            this.projectService = projectService;
        }

        // AssignDeveloperTicket & ListProjectTickets
        // ListSubmitterTickets, ListDeveloperTickets, ListProjectManagerTicket
        public async Task AssignDeveloperTicketAsync( string userId, int ticketId )
        {
            Ticket ticket = await this.context.Tickets.FindAsync( ticketId ).ConfigureAwait( false );
            ticket.DeveloperUserId = userId;
            await this.context.SaveChangesAsync( ).ConfigureAwait( false );
        }

        public List<Ticket> ListSubmitterTickets( string userId )
        {
            List<Ticket> tickets = ( List<Ticket> )this.context.Tickets.Where( t => t.OwnerUserId == userId )
                                                       .Include( t => t.DeveloperUser )
                                                       .Include( t => t.OwnerUser )
                                                       .Include( t => t.Project )
                                                       .Include( t => t.TicketPriority )
                                                       .Include( t => t.TicketStatus )
                                                       .Include( t => t.TicketType )
                                                       .ToList( );

            return tickets;
        }

        public List<Ticket> ListDeveloperTickets( string userId )
        {
            List<Ticket> tickets = ( List<Ticket> )this.context.Tickets.Where( t => t.DeveloperUserId == userId )
                                                       .Include( t => t.DeveloperUser )
                                                       .Include( t => t.OwnerUser )
                                                       .Include( t => t.Project )
                                                       .Include( t => t.TicketPriority )
                                                       .Include( t => t.TicketStatus )
                                                       .Include( t => t.TicketType )
                                                       .ToList( );

            return tickets;
        }

        public List<Ticket> ListProjectManagerTickets( string userId )
        {
            List<ProjectUser> userProjects = this.context.ProjectUsers.Where( pu => pu.UserId == userId ).ToList( );
            List<Ticket>      tickets      = new List<Ticket>( );
            this.context.Tickets.Include( t => t.DeveloperUser )
                .Include( t => t.OwnerUser )
                .Include( t => t.Project )
                .Include( t => t.TicketPriority )
                .Include( t => t.TicketStatus )
                .Include( t => t.TicketType );

            List<int> projectIds = userProjects.Select( record => record.ProjectId ).ToList( );

            foreach ( int id in projectIds )
            {
                tickets.AddRange(
                                 this.context.Tickets.Where( t => t.ProjectId == id )
                                     .Include( t => t.DeveloperUser )
                                     .Include( t => t.OwnerUser )
                                     .Include( t => t.Project )
                                     .Include( t => t.TicketPriority )
                                     .Include( t => t.TicketStatus )
                                     .Include( t => t.TicketType )
                                     .ToList( )
                                     .ToList( ) );
            }

            return tickets;
        }

        public async Task<List<Ticket>> ListProjectTicketsAsync( string userId )
        {
            List<Project> projects = ( List<Project> )await this.projectService.ListUserProjectsAsync( userId ).ConfigureAwait(false);
            List<Ticket>  tickets  = projects
                                     .SelectMany( t => t.Tickets )
                                     .ToList( );

            return tickets;
        }
    }
}
