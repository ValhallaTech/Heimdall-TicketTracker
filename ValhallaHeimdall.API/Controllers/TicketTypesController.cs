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
    public class TicketTypesController : Controller
    {
        private readonly ApplicationDbContext context;

        public TicketTypesController( ApplicationDbContext context ) => this.context = context;

        // GET: TicketTypes
        public async Task<IActionResult> Index( ) => this.View( await this.context.TicketTypes.ToListAsync( ).ConfigureAwait( false ) );

        // GET: TicketTypes/Details/5
        public async Task<IActionResult> Details( int? id )
        {
            if ( id == null )
            {
                return this.NotFound( );
            }

            TicketType ticketType = await this.context.TicketTypes
                                              .FirstOrDefaultAsync( m => m.Id == id )
                                              .ConfigureAwait( false );

            if ( ticketType == null )
            {
                return this.NotFound( );
            }

            return this.View( ticketType );
        }

        // GET: TicketTypes/Create
        public IActionResult Create( ) => this.View( );

        // POST: TicketTypes/Create
        // To protect from overposting attacks, enable the specific properties you want to bind to, for
        // more details, see http://go.microsoft.com/fwlink/?LinkId=317598.
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Create( [Bind( "Id,Name" )] TicketType ticketType )
        {
            if ( this.ModelState.IsValid )
            {
                this.context.Add( ticketType );
                await this.context.SaveChangesAsync( ).ConfigureAwait( false );

                return this.RedirectToAction( nameof( this.Index ) );
            }

            return this.View( ticketType );
        }

        // GET: TicketTypes/Edit/5
        public async Task<IActionResult> Edit( int? id )
        {
            if ( id == null )
            {
                return this.NotFound( );
            }

            TicketType ticketType = await this.context.TicketTypes.FindAsync( id ).ConfigureAwait( false );

            if ( ticketType == null )
            {
                return this.NotFound( );
            }

            return this.View( ticketType );
        }

        // POST: TicketTypes/Edit/5
        // To protect from overposting attacks, enable the specific properties you want to bind to, for
        // more details, see http://go.microsoft.com/fwlink/?LinkId=317598.
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Edit( int id, [Bind( "Id,Name" )] TicketType ticketType )
        {
            if ( id != ticketType.Id )
            {
                return this.NotFound( );
            }

            if ( this.ModelState.IsValid )
            {
                try
                {
                    this.context.Update( ticketType );
                    await this.context.SaveChangesAsync( ).ConfigureAwait( false );
                }
                catch ( DbUpdateConcurrencyException ) when ( !this.TicketTypeExists( ticketType.Id ) )
                {
                    return this.NotFound( );
                }

                return this.RedirectToAction( nameof( this.Index ) );
            }

            return this.View( ticketType );
        }

        // GET: TicketTypes/Delete/5
        public async Task<IActionResult> Delete( int? id )
        {
            if ( id == null )
            {
                return this.NotFound( );
            }

            TicketType ticketType = await this.context.TicketTypes
                                              .FirstOrDefaultAsync( m => m.Id == id )
                                              .ConfigureAwait( false );

            if ( ticketType == null )
            {
                return this.NotFound( );
            }

            return this.View( ticketType );
        }

        // POST: TicketTypes/Delete/5
        [HttpPost]
        [ActionName( "Delete" )]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteConfirmed( int id )
        {
            TicketType ticketType = await this.context.TicketTypes.FindAsync( id ).ConfigureAwait( false );
            this.context.TicketTypes.Remove( ticketType );
            await this.context.SaveChangesAsync( ).ConfigureAwait( false );

            return this.RedirectToAction( nameof( this.Index ) );
        }

        private bool TicketTypeExists( int id )
        {
            return this.context.TicketTypes.Any( e => e.Id == id );
        }
    }
}
