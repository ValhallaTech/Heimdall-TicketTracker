using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ValhallaHeimdall.BLL.Models;
using ValhallaHeimdall.DAL.Data;

namespace ValhallaHeimdall.API.Controllers
{
    [Authorize]
    public class TicketStatusController : Controller
    {
        private readonly ApplicationDbContext context;

        public TicketStatusController( ApplicationDbContext context ) => this.context = context;

        // GET: TicketStatus
        public async Task<IActionResult> Index( ) => this.View( await this.context.TicketStatuses.ToListAsync( ).ConfigureAwait( false ) );

        // GET: TicketStatus/Details/5
        public async Task<IActionResult> Details( int? id )
        {
            if ( id == null )
            {
                return this.NotFound( );
            }

            TicketStatus ticketStatus = await this.context.TicketStatuses
                                                  .FirstOrDefaultAsync( m => m.Id == id )
                                                  .ConfigureAwait( false );

            if ( ticketStatus == null )
            {
                return this.NotFound( );
            }

            return this.View( ticketStatus );
        }

        // GET: TicketStatus/Create
        public IActionResult Create( ) => this.View( );

        // POST: TicketStatus/Create
        // To protect from overposting attacks, enable the specific properties you want to bind to, for
        // more details, see http://go.microsoft.com/fwlink/?LinkId=317598.
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Create( [Bind( "Id,Name" )] TicketStatus ticketStatus )
        {
            if ( this.ModelState.IsValid )
            {
                await context.AddAsync( ticketStatus ).ConfigureAwait( false );
                await this.context.SaveChangesAsync( ).ConfigureAwait( false );

                return this.RedirectToAction( nameof( this.Index ) );
            }

            return this.View( ticketStatus );
        }

        // GET: TicketStatus/Edit/5
        public async Task<IActionResult> Edit( int? id )
        {
            if ( id == null )
            {
                return this.NotFound( );
            }

            TicketStatus ticketStatus = await this.context.TicketStatuses.FindAsync( id ).ConfigureAwait( false );

            if ( ticketStatus == null )
            {
                return this.NotFound( );
            }

            return this.View( ticketStatus );
        }

        // POST: TicketStatus/Edit/5
        // To protect from overposting attacks, enable the specific properties you want to bind to, for
        // more details, see http://go.microsoft.com/fwlink/?LinkId=317598.
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Edit( int id, [Bind( "Id,Name" )] TicketStatus ticketStatus )
        {
            if ( id != ticketStatus.Id )
            {
                return this.NotFound( );
            }

            if ( this.ModelState.IsValid )
            {
                try
                {
                    this.context.Update( ticketStatus );
                    await this.context.SaveChangesAsync( ).ConfigureAwait( false );
                }
                catch ( DbUpdateConcurrencyException ) when ( !this.TicketStatusExists( ticketStatus.Id ) )
                {
                    return this.NotFound( );
                }

                return this.RedirectToAction( nameof( this.Index ) );
            }

            return this.View( ticketStatus );
        }

        // GET: TicketStatus/Delete/5
        public async Task<IActionResult> Delete( int? id )
        {
            if ( id == null )
            {
                return this.NotFound( );
            }

            TicketStatus ticketStatus = await this.context.TicketStatuses
                                                  .FirstOrDefaultAsync( m => m.Id == id )
                                                  .ConfigureAwait( false );

            if ( ticketStatus == null )
            {
                return this.NotFound( );
            }

            return this.View( ticketStatus );
        }

        // POST: TicketStatus/Delete/5
        [HttpPost]
        [ActionName( "Delete" )]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteConfirmed( int id )
        {
            TicketStatus ticketStatus = await this.context.TicketStatuses.FindAsync( id ).ConfigureAwait( false );
            this.context.TicketStatuses.Remove( ticketStatus );
            await this.context.SaveChangesAsync( ).ConfigureAwait( false );

            return this.RedirectToAction( nameof( this.Index ) );
        }

        private bool TicketStatusExists( int id )
        {
            return this.context.TicketStatuses.Any( e => e.Id == id );
        }
    }
}
