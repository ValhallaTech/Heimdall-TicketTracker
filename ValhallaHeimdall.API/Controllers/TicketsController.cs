using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Query;
using ValhallaHeimdall.API.Services;
using ValhallaHeimdall.BLL.Models;
using ValhallaHeimdall.DAL.Data;

namespace ValhallaHeimdall.API.Controllers
{
    [Authorize]
    public class TicketsController : Controller
    {
        private readonly ApplicationDbContext context;

        private readonly IHeimdallHistoryService historyService;

        private readonly IHeimdallProjectService projectService;

        private readonly IHeimdallRolesService rolesService;

        private readonly UserManager<HeimdallUser> userManager;

        private readonly HeimdallAccessService accessService;

        public TicketsController(
            ApplicationDbContext      context,
            IHeimdallHistoryService   historyService,
            UserManager<HeimdallUser> userManager,
            HeimdallAccessService     accessService,
            IHeimdallProjectService   projectService,
            IHeimdallRolesService     rolesService )
        {
            this.context        = context;
            this.historyService = historyService;
            this.userManager    = userManager;
            this.accessService  = accessService;
            this.projectService = projectService;
            this.rolesService   = rolesService;
        }

        // GET: Tickets
        public async Task<IActionResult> Index( )
        {
            IIncludableQueryable<Ticket, TicketType> applicationDbContext = this.context.Tickets.Include( t => t.DeveloperUser )
                                                                                .Include( t => t.OwnerUser )
                                                                                .Include( t => t.Project )
                                                                                .Include( t => t.TicketPriority )
                                                                                .Include( t => t.TicketStatus )
                                                                                .Include( t => t.TicketType );

            return this.View( await applicationDbContext.ToListAsync( ).ConfigureAwait( false ) );
        }

        [Authorize( Roles = "ProjectManager" )]
        public async Task<IActionResult> MyProjects( )
        {
            string userId = this.userManager.GetUserId( this.User );
            List<ProjectUser> projectUserRecords = await this.context.ProjectUsers.Where( p => p.UserId == userId )
                                                             .Include( pu => pu.Project )
                                                             .ToListAsync( )
                                                             .ConfigureAwait( false );
            List<Project> projects = new List<Project>( );

            foreach ( ProjectUser projectUserRecord in projectUserRecords )
            {
                projects.Add( projectUserRecord.Project );
            }

            return this.View( projects );
        }

        [Authorize( Roles = "ProjectManager,Developer" )]
        public async Task<IActionResult> ProjectTickets( )
        {
            string userId = this.userManager.GetUserId( this.User );
            List<ProjectUser> projectUsers = await this.context.ProjectUsers.Where( p => p.UserId == userId )
                                                       .ToListAsync( )
                                                       .ConfigureAwait( false );
            List<int> projectIds = new List<int>( );

            foreach ( ProjectUser projectUser in projectUsers )
            {
                projectIds.Add( projectUser.ProjectId );
            }

            List<Project> projects = new List<Project>( );

            foreach ( int id in projectIds )
            {
                projects.Add(
                             await this.context.Projects.Include( p => p.Tickets )
                                       .ThenInclude( t => t.TicketType )
                                       .Include( p => p.Tickets )
                                       .ThenInclude( t => t.TicketPriority )
                                       .Include( p => p.Tickets )
                                       .ThenInclude( t => t.TicketStatus )
                                       .Include( p => p.Tickets )
                                       .ThenInclude( t => t.TicketType )
                                       .Include( p => p.Tickets )
                                       .ThenInclude( t => t.DeveloperUser )
                                       .Include( p => p.Tickets )
                                       .ThenInclude( t => t.OwnerUser )
                                       .FirstOrDefaultAsync( p => p.Id == id )
                                       .ConfigureAwait( false ) );
            }

            return this.View( projects );
        }

        [Authorize( Roles = "Developer" )]
        public async Task<IActionResult> MyTickets( )
        {
            string userId = this.userManager.GetUserId( this.User );
            List<Ticket> tickets = await this.context.Tickets.Where( t => t.DeveloperUserId == userId )
                                             .Include( t => t.DeveloperUser )
                                             .Include( t => t.OwnerUser )
                                             .Include( t => t.Project )
                                             .Include( t => t.TicketPriority )
                                             .Include( t => t.TicketStatus )
                                             .Include( t => t.TicketType )
                                             .Include( t => t.Comments )
                                             .ThenInclude( tc => tc.User )
                                             .Include( t => t.Attachments )
                                             .ToListAsync( )
                                             .ConfigureAwait( false );

            return View( tickets );
        }

        [Authorize( Roles = "Submitter" )]
        public async Task<IActionResult> CreatedTickets( )
        {
            string userId = this.userManager.GetUserId( this.User );
            List<Ticket> tickets = await this.context.Tickets.Where( t => t.OwnerUserId == userId )
                                             .Include( t => t.DeveloperUser )
                                             .Include( t => t.OwnerUser )
                                             .Include( t => t.Project )
                                             .Include( t => t.TicketPriority )
                                             .Include( t => t.TicketStatus )
                                             .Include( t => t.TicketType )
                                             .Include( t => t.Comments )
                                             .ThenInclude( tc => tc.User )
                                             .Include( t => t.Attachments )
                                             .ToListAsync( )
                                             .ConfigureAwait( false );

            return View( tickets );
        }

        // GET: Tickets/Details/5
        public async Task<IActionResult> Details( int? id )
        {
            if ( id == null )
            {
                return this.NotFound( );
            }

            // var userId   = this.userManager.GetUserId( User );
            // var roleName = await this.userManager.GetRolesAsync( await this.userManager.GetUserAsync( User ) );
            Ticket ticket = await this.context.Tickets.Include( t => t.DeveloperUser )
                                      .Include( t => t.OwnerUser )
                                      .Include( t => t.Project )
                                      .Include( t => t.TicketPriority )
                                      .Include( t => t.TicketStatus )
                                      .Include( t => t.TicketType )
                                      .Include( t => t.Comments )
                                      .ThenInclude( tc => tc.User )
                                      .Include( t => t.Attachments )
                                      .Include( t => t.Histories )
                                      .FirstOrDefaultAsync( m => m.Id == id )
                                      .ConfigureAwait( false );

            if ( ticket == null )
            {
                return this.NotFound( );
            }

            return this.View( ticket );
        }

        public async Task<IActionResult> ProjectTicketDetails( int? id )
        {
            if ( id == null )
            {
                return this.NotFound( );
            }

            Ticket ticket = await this.context.Tickets.Include( t => t.DeveloperUser )
                                      .Include( t => t.OwnerUser )
                                      .Include( t => t.Project )
                                      .Include( t => t.TicketPriority )
                                      .Include( t => t.TicketStatus )
                                      .Include( t => t.TicketType )
                                      .Include( t => t.Comments )
                                      .ThenInclude( tc => tc.User )
                                      .Include( t => t.Attachments )
                                      .FirstOrDefaultAsync( m => m.Id == id )
                                      .ConfigureAwait( false );

            if ( ticket == null )
            {
                return this.NotFound( );
            }

            return this.View( ticket );
        }

        // GET: Tickets/Create
        public IActionResult Create( )
        {
            this.ViewData["DeveloperUserId"]  = new SelectList( this.context.Users,            "Id", "Id" );
            this.ViewData["OwnerUserId"]      = new SelectList( this.context.Users,            "Id", "Id" );
            this.ViewData["ProjectId"]        = new SelectList( this.context.Projects,         "Id", "Name" );
            this.ViewData["TicketPriorityId"] = new SelectList( this.context.TicketPriorities, "Id", "Id" );
            this.ViewData["TicketStatusId"]   = new SelectList( this.context.TicketStatuses,   "Id", "Id" );
            this.ViewData["TicketTypeId"]     = new SelectList( this.context.TicketTypes,      "Id", "Id" );

            return this.View( );
        }

        // POST: Tickets/Create
        // To protect from overposting attacks, enable the specific properties you want to bind to, for
        // more details, see http://go.microsoft.com/fwlink/?LinkId=317598.
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Create(
            [Bind(
                     "Id,Title,Description,Created,Updated,ProjectId,TicketTypeId,TicketPriorityId,TicketStatusId,OwnerUserId,DeveloperUserId" )]
            Ticket ticket )
        {
            // ticket.OwnerUserId = await this.userManager.GetUserIdAsync( User ).ConfigureAwait( false );

            // if ( this.ModelState.IsValid )
            // {
            // if ( attachment != null )
            // {
            // AttachmentHandler attachmentHandler = new AttachmentHandler( );
            // ticket.Attachments.Add( attachmentHandler.Attach( attachment ) );
            // }
            // else
            // {
            // this.context.Add( ticket );
            // await this.context.SaveChangesAsync( ).ConfigureAwait( false );

            // return this.RedirectToAction( nameof( this.Index ) );
            // }
            // }
            this.ViewData["DeveloperUserId"] = new SelectList( this.context.Users, "Id", "Id", ticket.DeveloperUserId );
            this.ViewData["OwnerUserId"]     = new SelectList( this.context.Users, "Id", "Id", ticket.OwnerUserId );
            this.ViewData["ProjectId"]       = new SelectList( this.context.Projects, "Id", "Name", ticket.ProjectId );
            this.ViewData["TicketPriorityId"] = new SelectList(
                                                               this.context.TicketPriorities,
                                                               "Id",
                                                               "Id",
                                                               ticket.TicketPriorityId );
            this.ViewData["TicketStatusId"] = new SelectList(
                                                             this.context.TicketStatuses,
                                                             "Id",
                                                             "Id",
                                                             ticket.TicketStatusId );
            this.ViewData["TicketTypeId"] = new SelectList( this.context.TicketTypes, "Id", "Id", ticket.TicketTypeId );

            return this.View( ticket );
        }

        // GET: Tickets/Edit/5
        [Authorize( Roles = "Admin,ProjectManager,Developer" )]
        public async Task<IActionResult> Edit( int? id )
        {
            if ( id == null )
            {
                return this.NotFound( );
            }

            Ticket ticket = await this.context.Tickets.FindAsync( id ).ConfigureAwait( false );

            if ( ticket == null )
            {
                return this.NotFound( );
            }

            this.ViewData["DeveloperUserId"] = new SelectList( this.context.Users, "Id", "Id", ticket.DeveloperUserId );
            this.ViewData["OwnerUserId"]     = new SelectList( this.context.Users, "Id", "Id", ticket.OwnerUserId );
            this.ViewData["ProjectId"]       = new SelectList( this.context.Projects, "Id", "Name", ticket.ProjectId );
            this.ViewData["TicketPriorityId"] = new SelectList(
                                                               this.context.TicketPriorities,
                                                               "Id",
                                                               "Id",
                                                               ticket.TicketPriorityId );
            this.ViewData["TicketStatusId"] = new SelectList(
                                                             this.context.TicketStatuses,
                                                             "Id",
                                                             "Id",
                                                             ticket.TicketStatusId );
            this.ViewData["TicketTypeId"] = new SelectList( this.context.TicketTypes, "Id", "Id", ticket.TicketTypeId );

            return this.View( ticket );
        }

        // POST: Tickets/Edit/5
        // To protect from overposting attacks, enable the specific properties you want to bind to, for
        // more details, see http://go.microsoft.com/fwlink/?LinkId=317598.
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Edit(
            int id,
            [Bind(
                     "Id,Title,Description,Created,Updated,ProjectId,TicketTypeId,TicketPriorityId,TicketStatusId,OwnerUserId,DeveloperUserId" )]
            Ticket ticket )
        {
            if ( id != ticket.Id )
            {
                return this.NotFound( );
            }

            Ticket oldTicket = await this.context.Tickets.AsNoTracking( )
                                         .FirstOrDefaultAsync( t => t.Id == ticket.Id )
                                         .ConfigureAwait( false );

            if ( this.ModelState.IsValid )
            {
                // try
                // {
                // if ( attachment != null )
                // {
                // AttachmentHandler attachmentHandler = new AttachmentHandler( );
                // ticket.Attachments.Add( attachmentHandler.Attach( attachment ) );
                // }

                // this.context.Update( ticket );
                // await this.context.SaveChangesAsync( ).ConfigureAwait( false );
                // }
                // catch ( DbUpdateConcurrencyException )
                // {
                // if ( !this.TicketExists( ticket.Id ) )
                // {
                // return this.NotFound( );
                // }
                // else
                // {
                // throw;
                // }
                // }

                // Add History
                string userId = this.userManager.GetUserId( this.User );
                Ticket newTicket = await this.context.Tickets.Include( t => t.TicketPriority )
                                             .Include( t => t.TicketStatus )
                                             .Include( t => t.TicketType )
                                             .Include( t => t.Project )
                                             .AsNoTracking( )
                                             .FirstOrDefaultAsync( t => t.Id == ticket.Id )
                                             .ConfigureAwait( false );
                await this.historyService.AddHistoryAsync( oldTicket, ticket, userId ).ConfigureAwait( false );

                return this.RedirectToAction( nameof( this.Index ) );
            }

            this.ViewData["DeveloperUserId"] = new SelectList( this.context.Users, "Id", "Id", ticket.DeveloperUserId );
            this.ViewData["OwnerUserId"]     = new SelectList( this.context.Users, "Id", "Id", ticket.OwnerUserId );
            this.ViewData["ProjectId"]       = new SelectList( this.context.Projects, "Id", "Name", ticket.ProjectId );
            this.ViewData["TicketPriorityId"] = new SelectList(
                                                               this.context.TicketPriorities,
                                                               "Id",
                                                               "Id",
                                                               ticket.TicketPriorityId );
            this.ViewData["TicketStatusId"] = new SelectList(
                                                             this.context.TicketStatuses,
                                                             "Id",
                                                             "Id",
                                                             ticket.TicketStatusId );
            this.ViewData["TicketTypeId"] = new SelectList( this.context.TicketTypes, "Id", "Id", ticket.TicketTypeId );

            return this.View( ticket );
        }

        // GET: Tickets/Delete/5
        [Authorize( Roles = "Admin,ProjectManager,Developer" )]
        public async Task<IActionResult> Delete( int? id )
        {
            if ( id == null )
            {
                return this.NotFound( );
            }

            Ticket ticket = await this.context.Tickets.Include( t => t.DeveloperUser )
                                      .Include( t => t.OwnerUser )
                                      .Include( t => t.Project )
                                      .Include( t => t.TicketPriority )
                                      .Include( t => t.TicketStatus )
                                      .Include( t => t.TicketType )
                                      .FirstOrDefaultAsync( m => m.Id == id )
                                      .ConfigureAwait( false );

            if ( ticket == null )
            {
                return this.NotFound( );
            }

            return this.View( ticket );
        }

        // POST: Tickets/Delete/5
        [HttpPost]
        [ActionName( "Delete" )]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteConfirmed( int id )
        {
            Ticket ticket = await this.context.Tickets.FindAsync( id ).ConfigureAwait( false );
            this.context.Tickets.Remove( ticket );
            await this.context.SaveChangesAsync( ).ConfigureAwait( false );

            return this.RedirectToAction( nameof( this.Index ) );
        }

        private bool TicketExists( int id )
        {
            return this.context.Tickets.Any( e => e.Id == id );
        }
    }
}
