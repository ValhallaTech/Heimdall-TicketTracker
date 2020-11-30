using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Query;
using ValhallaHeimdall.BLL.Models;
using ValhallaHeimdall.DAL.Data;

namespace ValhallaHeimdall.API.Controllers
{
    [Authorize( Roles = "Administrator" )]
    public class TicketHistoriesController : Controller
    {
        private readonly ApplicationDbContext context;

        public TicketHistoriesController( ApplicationDbContext context ) => this.context = context;

        // GET: TicketHistories
        public async Task<IActionResult> Index( )
        {
            IIncludableQueryable<TicketHistory, HeimdallUser> applicationDbContext = this.context.TicketHistories
                                                                                         .Include( t => t.Ticket )
                                                                                         .Include( t => t.User );

            return this.View( await applicationDbContext.ToListAsync( ).ConfigureAwait( false ) );
        }

        // GET: TicketHistories/Details/5
        public async Task<IActionResult> Details( int? id )
        {
            if ( id == null )
            {
                return this.NotFound( );
            }

            TicketHistory ticketHistory = await this.context.TicketHistories
                                                    .Include( t => t.Ticket )
                                                    .Include( t => t.User )
                                                    .FirstOrDefaultAsync( m => m.Id == id )
                                                    .ConfigureAwait( false );

            if ( ticketHistory == null )
            {
                return this.NotFound( );
            }

            return this.View( ticketHistory );
        }

        // GET: TicketHistories/Create
        public IActionResult Create( )
        {
            this.ViewData["TicketId"] = new SelectList( this.context.Tickets, "Id", "Description" );
            this.ViewData["UserId"]   = new SelectList( this.context.Users,   "Id", "Id" );

            return this.View( );
        }

        // POST: TicketHistories/Create
        // To protect from overposting attacks, enable the specific properties you want to bind to, for
        // more details, see http://go.microsoft.com/fwlink/?LinkId=317598.
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Create( [Bind( "Id,TicketId,Property,OldValue,NewValue,Created,UserId" )]
                                                 TicketHistory ticketHistory )
        {
            if ( this.ModelState.IsValid )
            {
                this.context.Add( ticketHistory );
                await this.context
                          .SaveChangesAsync( )
                          .ConfigureAwait( false );

                return this.RedirectToAction( nameof( this.Index ) );
            }

            this.ViewData["TicketId"] =
                new SelectList( this.context.Tickets, "Id", "Description", ticketHistory.TicketId );
            this.ViewData["UserId"] = new SelectList( this.context.Users, "Id", "Id", ticketHistory.UserId );

            return this.View( ticketHistory );
        }

        // GET: TicketHistories/Edit/5
        public async Task<IActionResult> Edit( int? id )
        {
            if ( id == null )
            {
                return this.NotFound( );
            }

            TicketHistory ticketHistory = await this.context.TicketHistories
                                                    .FindAsync( id )
                                                    .ConfigureAwait( false );

            if ( ticketHistory == null )
            {
                return this.NotFound( );
            }

            this.ViewData["TicketId"] =
                new SelectList( this.context.Tickets, "Id", "Description", ticketHistory.TicketId );
            this.ViewData["UserId"] = new SelectList( this.context.Users, "Id", "Id", ticketHistory.UserId );

            return this.View( ticketHistory );
        }

        // POST: TicketHistories/Edit/5
        // To protect from overposting attacks, enable the specific properties you want to bind to, for
        // more details, see http://go.microsoft.com/fwlink/?LinkId=317598.
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Edit( int id,
                                               [Bind( "Id,TicketId,Property,OldValue,NewValue,Created,UserId" )]
                                               TicketHistory ticketHistory )
        {
            if ( id != ticketHistory.Id )
            {
                return this.NotFound( );
            }

            if ( this.ModelState.IsValid )
            {
                try
                {
                    this.context.Update( ticketHistory );
                    await this.context
                              .SaveChangesAsync( )
                              .ConfigureAwait( false );
                }
                catch ( DbUpdateConcurrencyException ) when ( !this.TicketHistoryExists( ticketHistory.Id ) )
                {
                    return this.NotFound( );
                }

                return this.RedirectToAction( nameof( this.Index ) );
            }

            this.ViewData["TicketId"] =
                new SelectList( this.context.Tickets, "Id", "Description", ticketHistory.TicketId );
            this.ViewData["UserId"] = new SelectList( this.context.Users, "Id", "Id", ticketHistory.UserId );

            return this.View( ticketHistory );
        }

        // GET: TicketHistories/Delete/5
        public async Task<IActionResult> Delete( int? id )
        {
            if ( id == null )
            {
                return this.NotFound( );
            }

            TicketHistory ticketHistory = await this.context.TicketHistories
                                                    .Include( t => t.Ticket )
                                                    .Include( t => t.User )
                                                    .FirstOrDefaultAsync( m => m.Id == id )
                                                    .ConfigureAwait( false );

            if ( ticketHistory == null )
            {
                return this.NotFound( );
            }

            return this.View( ticketHistory );
        }

        // POST: TicketHistories/Delete/5
        [HttpPost]
        [ActionName( "Delete" )]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteConfirmed( int id )
        {
            TicketHistory ticketHistory = await this.context.TicketHistories
                                                    .FindAsync( id )
                                                    .ConfigureAwait( false );
            this.context.TicketHistories.Remove( ticketHistory );
            await this.context
                      .SaveChangesAsync( )
                      .ConfigureAwait( false );

            return this.RedirectToAction( nameof( this.Index ) );
        }

        private bool TicketHistoryExists( int id )
        {
            return this.context.TicketHistories.Any( e => e.Id == id );
        }
    }
}
