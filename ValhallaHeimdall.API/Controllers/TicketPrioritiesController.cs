using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ValhallaHeimdall.BLL.Models;
using ValhallaHeimdall.DAL.Data;

namespace ValhallaHeimdall.API.Controllers
{
    [Authorize( Roles = "Administrator" )]
    public class TicketPrioritiesController : Controller
    {
        private readonly ApplicationDbContext context;

        public TicketPrioritiesController( ApplicationDbContext context ) => this.context = context;

        // GET: TicketPriorities
        public async Task<IActionResult> Index( ) => this.View( await this.context.TicketPriorities.ToListAsync( ).ConfigureAwait( false ) );

        // GET: TicketPriorities/Details/5
        public async Task<IActionResult> Details( int? id )
        {
            if ( id == null )
            {
                return this.NotFound( );
            }

            TicketPriority ticketPriority = await this.context.TicketPriorities
                                                      .FirstOrDefaultAsync( m => m.Id == id )
                                                      .ConfigureAwait( false );

            if ( ticketPriority == null )
            {
                return this.NotFound( );
            }

            return this.View( ticketPriority );
        }

        // POST: TicketPriorities/Create
        // To protect from overposting attacks, enable the specific properties you want to bind to, for
        // more details, see http://go.microsoft.com/fwlink/?LinkId=317598.
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Create( [Bind( "Id,Name" )] TicketPriority ticketPriority )
        {
            if ( !this.ModelState.IsValid )
            {
                return this.View( ticketPriority );
            }

            this.context.Add( ticketPriority );
            await this.context
                      .SaveChangesAsync( )
                      .ConfigureAwait( false );

            return this.RedirectToAction( nameof( this.Index ) );
        }

        // GET: TicketPriorities/Edit/5
        public async Task<IActionResult> Edit( int? id )
        {
            if ( id == null )
            {
                return this.NotFound( );
            }

            TicketPriority ticketPriority = await this.context.TicketPriorities
                                                      .FindAsync( id )
                                                      .ConfigureAwait( false );

            if ( ticketPriority == null )
            {
                return this.NotFound( );
            }

            return this.View( ticketPriority );
        }

        // POST: TicketPriorities/Edit/5
        // To protect from overposting attacks, enable the specific properties you want to bind to, for
        // more details, see http://go.microsoft.com/fwlink/?LinkId=317598.
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Edit( int id, [Bind( "Id,Name" )] TicketPriority ticketPriority )
        {
            if ( id != ticketPriority.Id )
            {
                return this.NotFound( );
            }

            if ( !this.ModelState.IsValid )
            {
                return this.View( ticketPriority );
            }

            try
            {
                this.context.Update( ticketPriority );
                await this.context
                          .SaveChangesAsync( )
                          .ConfigureAwait( false );
            }
            catch ( DbUpdateConcurrencyException )
            {
                if ( !this.TicketPriorityExists( ticketPriority.Id ) )
                {
                    return this.NotFound( );
                }
                else
                {
                    throw;
                }
            }

            return this.RedirectToAction( nameof( this.Index ) );

        }

        // GET: TicketPriorities/Delete/5
        public async Task<IActionResult> Delete( int? id )
        {
            if ( id == null )
            {
                return this.NotFound( );
            }

            TicketPriority ticketPriority = await this.context.TicketPriorities
                                                      .FirstOrDefaultAsync( m => m.Id == id )
                                                      .ConfigureAwait( false );

            if ( ticketPriority == null )
            {
                return this.NotFound( );
            }

            return this.View( ticketPriority );
        }

        // POST: TicketPriorities/Delete/5
        [HttpPost]
        [ActionName( "Delete" )]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteConfirmed( int id )
        {
            TicketPriority ticketPriority = await this.context.TicketPriorities.FindAsync( id ).ConfigureAwait( false );
            this.context.TicketPriorities.Remove( ticketPriority );
            await this.context.SaveChangesAsync( ).ConfigureAwait( false );

            return this.RedirectToAction( nameof( this.Index ) );
        }

        private bool TicketPriorityExists( int id )
        {
            return this.context.TicketPriorities.Any( e => e.Id == id );
        }
    }
}
