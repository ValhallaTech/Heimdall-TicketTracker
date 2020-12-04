using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Query;
using ValhallaHeimdall.BLL.Models;
using ValhallaHeimdall.DAL.Data;

namespace ValhallaHeimdall.API.Controllers
{
    [Authorize]
    public class TicketCommentsController : Controller
    {
        private readonly ApplicationDbContext context;

        private readonly UserManager<HeimdallUser> userManager;

        public TicketCommentsController( ApplicationDbContext context, UserManager<HeimdallUser> userManager )
        {
            this.context     = context;
            this.userManager = userManager;
        }

        // GET: TicketComments
        public async Task<IActionResult> Index( )
        {
            IIncludableQueryable<TicketComment, HeimdallUser> applicationDbContext = this.context.TicketComments.Include( t => t.Ticket ).Include( t => t.User );

            return this.View( await applicationDbContext.ToListAsync( ).ConfigureAwait( false ) );
        }

        // GET: TicketComments/Details/5
        public async Task<IActionResult> Details( int? id )
        {
            if ( id == null )
            {
                return this.NotFound( );
            }

            TicketComment ticketComment = await this.context.TicketComments
                                                    .Include( t => t.Ticket )
                                                    .Include( t => t.User )
                                                    .FirstOrDefaultAsync( m => m.Id == id )
                                                    .ConfigureAwait( false );

            if ( ticketComment == null )
            {
                return this.NotFound( );
            }

            return this.View( ticketComment );
        }

        // GET: TicketComments/Create
        public IActionResult Create( int? id )
        {
            TicketComment model = new TicketComment { TicketId = ( int )id };

            this.ViewData["TicketId"] = new SelectList( this.context.Tickets, "Id", "Description" );
            this.ViewData["UserId"]   = new SelectList( this.context.Users,   "Id", "Id" );

            return this.View( model );
        }

        // POST: TicketComments/Create
        // To protect from overposting attacks, enable the specific properties you want to bind to, for
        // more details, see http://go.microsoft.com/fwlink/?LinkId=317598.
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Create( [Bind( "Comment,Created,TicketId,UserId" )]
                                                 TicketComment ticketComment )
        {
            if ( this.ModelState.IsValid )
            {
                ticketComment.Created = DateTimeOffset.Now;
                ticketComment.UserId  = this.userManager.GetUserId( this.User );

                await context.AddAsync( ticketComment ).ConfigureAwait( false );
                await this.context.SaveChangesAsync( ).ConfigureAwait( false );

                return this.RedirectToAction( "Details", "Tickets", new { id = ticketComment.TicketId } );
            }
            else
            {
                return this.NotFound( );
            }

            // this.ViewData["TicketId"] = new SelectList(this.context.Tickets, "Id", "Description", ticketComment.TicketId);
            // this.ViewData["UserId"]   = new SelectList(this.context.Users,       "Id", "Id",          ticketComment.UserId);
            // return this.View(ticketComment);
        }

        // GET: TicketComments/Edit/5
        public async Task<IActionResult> Edit( int? id )
        {
            if ( id == null )
            {
                return this.NotFound( );
            }

            TicketComment ticketComment = await this.context.TicketComments
                                                    .FindAsync( id )
                                                    .ConfigureAwait( false );

            if ( ticketComment == null )
            {
                return this.NotFound( );
            }

            this.ViewData["TicketId"] =
                new SelectList( this.context.Tickets, "Id", "Description", ticketComment.TicketId );
            this.ViewData["UserId"] = new SelectList( this.context.Users, "Id", "Id", ticketComment.UserId );

            return this.View( ticketComment );
        }

        // POST: TicketComments/Edit/5
        // To protect from overposting attacks, enable the specific properties you want to bind to, for
        // more details, see http://go.microsoft.com/fwlink/?LinkId=317598.
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Edit( int id,
                                               [Bind( "Id,Comment,Created,TicketId,UserId" )]
                                               TicketComment ticketComment )
        {
            if ( id != ticketComment.Id )
            {
                return this.NotFound( );
            }

            if ( this.ModelState.IsValid )
            {
                try
                {
                    this.context.Update( ticketComment );
                    await this.context
                              .SaveChangesAsync( )
                              .ConfigureAwait( false );
                }
                catch ( DbUpdateConcurrencyException ) when ( !this.TicketCommentExists( ticketComment.Id ) )

                {
                    return this.NotFound( );
                }

                return this.RedirectToAction( nameof( this.Index ) );
            }

            this.ViewData["TicketId"] =
                new SelectList( this.context.Tickets, "Id", "Description", ticketComment.TicketId );
            this.ViewData["UserId"] = new SelectList( this.context.Users, "Id", "Id", ticketComment.UserId );

            return this.View( ticketComment );
        }

        // GET: TicketComments/Delete/5
        public async Task<IActionResult> Delete( int? id )
        {
            if ( id == null )
            {
                return this.NotFound( );
            }

            TicketComment ticketComment = await this.context.TicketComments
                                                    .Include( t => t.Ticket )
                                                    .Include( t => t.User )
                                                    .FirstOrDefaultAsync( m => m.Id == id )
                                                    .ConfigureAwait( false );

            if ( ticketComment == null )
            {
                return this.NotFound( );
            }

            return this.View( ticketComment );
        }

        // POST: TicketComments/Delete/5
        [HttpPost]
        [ActionName( "Delete" )]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteConfirmed( int id )
        {
            TicketComment ticketComment = await this.context.TicketComments
                                                    .FindAsync( id )
                                                    .ConfigureAwait( false );
            this.context.TicketComments.Remove( ticketComment );
            await this.context
                      .SaveChangesAsync( )
                      .ConfigureAwait( false );

            return this.RedirectToAction( nameof( this.Index ) );
        }

        private bool TicketCommentExists( int id )
        {
            return this.context.TicketComments.Any( e => e.Id == id );
        }
    }
}
