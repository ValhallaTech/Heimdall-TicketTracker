using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Dynamic.Core;
using System.Threading.Tasks;
using JM.LinqFaster;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Query;
using ValhallaHeimdall.API.Services;
using ValhallaHeimdall.API.Utilities;
using ValhallaHeimdall.BLL.Models;
using ValhallaHeimdall.BLL.Models.ViewModels;
using ValhallaHeimdall.DAL.Data;
using Z.EntityFramework.Plus;

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
            IIncludableQueryable<Ticket, TicketType> applicationDbContext = this.context.Tickets
                .Include( t => t.DeveloperUser )
                .Include( t => t.OwnerUser )
                .Include( t => t.Project )
                .Include( t => t.TicketPriority )
                .Include( t => t.TicketStatus )
                .Include( t => t.TicketType );

            return this.View( await applicationDbContext.ToListAsync( ).ConfigureAwait( false ) );
        }

        // GET My Tickets
        public async Task<IActionResult> MyTickets( )
        {
            string userId = this.userManager.GetUserId( this.User );
            IEnumerable<string> roleList = await this.rolesService
                                                     .ListUserRolesAsync(
                                                                         await this.context.Users.FindAsync( userId )
                                                                             .ConfigureAwait( false ) )
                                                     .ConfigureAwait( false );
            string       role = roleList.FirstOrDefault( );
            List<Ticket> model;

            switch ( role )
            {
                case "Administrator":
                    model = await this.context.Tickets.Include( t => t.OwnerUser )
                                      .Include( t => t.TicketPriority )
                                      .Include( t => t.TicketStatus )
                                      .Include( t => t.TicketType )
                                      .Include( t => t.Project )
                                      .ToListAsync( )
                                      .ConfigureAwait( false );

                    break;

                // Snippet to get ticket for project manager - special case for roles
                case "ProjectManager":

                    model = new List<Ticket>( );

                    List<ProjectUser> userProjects = await this.context.ProjectUsers.Where( pu => pu.UserId == userId )
                                                               .ToListAsync( )
                                                               .ConfigureAwait( false );

                    List<int> projectIds = userProjects
                                           .Select( record => this.context.Projects.Find( record.ProjectId ).Id )
                                           .ToList( );

                    foreach ( List<Ticket> tickets in
                        from int id in projectIds
                        let tickets = this.context.Tickets.Where( t => t.ProjectId == id )
                                          .Include( t => t.OwnerUser )
                                          .Include( t => t.TicketPriority )
                                          .Include( t => t.TicketStatus )
                                          .Include( t => t.TicketType )
                                          .Include( t => t.Project )
                                          .ToList( )
                        select tickets )
                    {
                        model.AddRange( tickets );
                    }

                    break;

                case "Developer":
                    model = this.context.Tickets.Where( t => t.DeveloperUserId == userId )
                                .Include( t => t.OwnerUser )
                                .Include( t => t.TicketPriority )
                                .Include( t => t.TicketStatus )
                                .Include( t => t.TicketType )
                                .Include( t => t.Project )
                                .ToList( );

                    break;

                case "Submitter":

                case "NewUser":
                    model = this.context.Tickets.Where( t => t.OwnerUserId == userId )
                                .Include( t => t.OwnerUser )
                                .Include( t => t.TicketPriority )
                                .Include( t => t.TicketStatus )
                                .Include( t => t.TicketType )
                                .Include( t => t.Project )
                                .ToList( );

                    break;

                default:
                    return this.RedirectToAction( "Index" );
            }

            return this.View( model );
        }

        // GET: Tickets/Details/5
        public async Task<IActionResult> Details( int? id )
        {
            if ( id == null )
            {
                return this.NotFound( );
            }

            TicketDetailsViewModel vm = new TicketDetailsViewModel( );
            Ticket ticket = await this.context.Tickets.IncludeOptimized( t => t.DeveloperUser )
                                      .IncludeOptimized( t => t.OwnerUser )
                                      .IncludeOptimized( t => t.Project )
                                      .IncludeOptimized( t => t.TicketPriority )
                                      .IncludeOptimized( t => t.TicketStatus )
                                      .IncludeOptimized( t => t.TicketType )
                                      .IncludeOptimized( t => t.Attachments )
                                      .IncludeOptimized( t => t.Comments )
                                      .FirstOrDefaultAsync( m => m.Id == id )
                                      .ConfigureAwait( false );

            if ( ticket == null )
            {
                return this.NotFound( );
            }

            vm.Ticket = ticket;

            return this.View( vm );
        }

        // GET: Tickets/Create
        public IActionResult Create( )
        {
            this.ViewData["DeveloperUserId"] = new SelectList( this.context.Users, "Id", "FullName" );
            this.ViewData["OwnerUserId"]     = new SelectList( this.context.Users, "Id", "FullName" );

            this.ViewData["ProjectId"]        = new SelectList( this.context.Projects,         "Id", "Name" );
            this.ViewData["TicketPriorityId"] = new SelectList( this.context.TicketPriorities, "Id", "Name" );
            this.ViewData["TicketStatusId"]   = new SelectList( this.context.TicketStatuses,   "Id", "Name" );
            this.ViewData["TicketTypeId"]     = new SelectList( this.context.TicketTypes,      "Id", "Name" );

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
            Ticket ticket,
            List<IFormFile> attachments )
        {
            if ( this.ModelState.IsValid )
            {
                // IF not demo user
                if ( !this.User.IsInRole( "DemoUser" ) )
                {
                    // Add file handler
                    ticket.OwnerUserId = this.userManager.GetUserId( User );
                    ticket.Created     = DateTime.Now;

                    if ( attachments != null )
                    {
                        foreach ( IFormFile attachment in attachments )
                        {
                            AttachmentHandler attachmentHandler = new AttachmentHandler( );
                            ticket.Attachments.Add( attachmentHandler.Attach( attachment, ticket.Id ) );
                        }
                    }

                    this.context.Add( ticket );
                    await this.context.SaveChangesAsync( ).ConfigureAwait( false );

                    return this.RedirectToAction( nameof( Index ) );
                }
                else
                {
                    // Handle tempdata["DemoLockout"]
                    this.TempData["DemoLockout"] =
                        "Your changes have not been saved. You must be logged in as a full user.";

                    // Handle redirect to index
                    return this.RedirectToAction( nameof( this.Index ) );
                }
            }

            this.ViewData["DeveloperUserId"] =
                new SelectList( this.context.Users, "Id", "FullName", ticket.DeveloperUserId );
            this.ViewData["OwnerUserId"] = new SelectList( this.context.Users,    "Id", "Id",   ticket.OwnerUserId );
            this.ViewData["ProjectId"]   = new SelectList( this.context.Projects, "Id", "Name", ticket.ProjectId );
            this.ViewData["TicketPriorityId"] = new SelectList(
                                                               this.context.TicketPriorities,
                                                               "Id",
                                                               "Name",
                                                               ticket.TicketPriorityId );
            this.ViewData["TicketStatusId"] = new SelectList(
                                                             this.context.TicketStatuses,
                                                             "Id",
                                                             "Name",
                                                             ticket.TicketStatusId );
            this.ViewData["TicketTypeId"] =
                new SelectList( this.context.TicketTypes, "Id", "Name", ticket.TicketTypeId );

            return this.View( ticket );
        }

        // GET: Tickets/Edit/5
        public async Task<IActionResult> Edit( int? id )
        {
            if ( id == null )
            {
                return NotFound( );
            }

            string userId = this.userManager.GetUserId( User );
            string roleName =
                ( await this.userManager
                            .GetRolesAsync( await this.userManager.GetUserAsync( User ).ConfigureAwait( false ) )
                            .ConfigureAwait( false ) ).FirstOrDefault( );

            // If you have access to a ticket
            if ( await this.accessService.CanInteractTicketAsync( userId, ( int )id, roleName )
                           .ConfigureAwait( false ) )
            {
                Ticket ticket = await this.context.Tickets.FindAsync( id ).ConfigureAwait( false );

                if ( ticket == null )
                {
                    return this.NotFound( );
                }

                ViewData["DeveloperUserId"] = new SelectList(
                                                             this.context.Users,
                                                             "Id",
                                                             "FullName",
                                                             ticket.DeveloperUserId );
                ViewData["OwnerUserId"] = new SelectList( this.context.Users,    "Id", "Id",   ticket.OwnerUserId );
                ViewData["ProjectId"]   = new SelectList( this.context.Projects, "Id", "Name", ticket.ProjectId );
                ViewData["TicketPriorityId"] = new SelectList(
                                                              this.context.TicketPriorities,
                                                              "Id",
                                                              "Name",
                                                              ticket.TicketPriorityId );
                ViewData["TicketStatusId"] = new SelectList(
                                                            this.context.TicketStatuses,
                                                            "Id",
                                                            "Name",
                                                            ticket.TicketStatusId );
                ViewData["TicketTypeId"] = new SelectList(
                                                          this.context.TicketTypes,
                                                          "Id",
                                                          "Name",
                                                          ticket.TicketTypeId );

                return this.View( ticket );
            }

            this.TempData["Nah"] = "Nah bruh...";

            return RedirectToAction( "Index" );
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
                try
                {
                    ticket.Updated = DateTime.Now;
                    this.context.Update( ticket );
                    await this.context.SaveChangesAsync( ).ConfigureAwait( false );
                }
                catch ( DbUpdateConcurrencyException ) when ( !this.TicketExists( ticket.Id ) )
                {
                    return this.NotFound( );
                }

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
        [HttpPost] [ActionName( "Delete" )] [ValidateAntiForgeryToken]
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