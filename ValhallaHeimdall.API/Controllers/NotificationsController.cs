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
    [Authorize]
    public class NotificationsController : Controller
    {
        private readonly ApplicationDbContext context;

        public NotificationsController( ApplicationDbContext context ) => this.context = context;

        // GET: Notifications
        public async Task<IActionResult> Index( )
        {
            IIncludableQueryable<Notification, HeimdallUser> applicationDbContext = this.context.Notifications.Include( n => n.Ticket ).Include( n => n.Sender );

            return View( await applicationDbContext.ToListAsync( ).ConfigureAwait( false ) );
        }

        // GET: Notifications/Details/5
        public async Task<IActionResult> Details( int? id )
        {
            if ( id == null )
            {
                return this.NotFound( );
            }

            Notification notification = await this.context.Notifications.Include( n => n.Ticket )
                                                  .Include( n => n.Sender )
                                                  .FirstOrDefaultAsync( m => m.Id == id )
                                                  .ConfigureAwait( false );

            if ( notification == null )
            {
                return this.NotFound( );
            }

            return View( notification );
        }

        // GET: Notifications/Create
        public IActionResult Create( )
        {
            this.ViewData["TicketId"] = new SelectList( this.context.Tickets, "Id", "Description" );
            this.ViewData["UserId"]   = new SelectList( this.context.Users,   "Id", "Id" );

            return this.View( );
        }

        // POST: Notifications/Create
        // To protect from over-posting attacks, enable the specific properties you want to bind to, for
        // more details, see http://go.microsoft.com/fwlink/?LinkId=317598.
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Create(
            [Bind( "Id,TicketId,Description,Created,UserId" )]
            Notification notification )
        {
            if ( this.ModelState.IsValid )
            {
                this.context.Add( notification );
                await this.context.SaveChangesAsync( ).ConfigureAwait( false );

                return this.RedirectToAction( nameof( this.Index ) );
            }

            this.ViewData["TicketId"] = new SelectList( this.context.Tickets, "Id", "Description", notification.TicketId );
            this.ViewData["UserId"]   = new SelectList( this.context.Users,   "Id", "Id",          notification.UserId );

            return View( notification );
        }

        // GET: Notifications/Edit/5
        public async Task<IActionResult> Edit( int? id )
        {
            if ( id == null )
            {
                return this.NotFound( );
            }

            Notification notification = await this.context.Notifications.FindAsync( id ).ConfigureAwait( false );

            if ( notification == null )
            {
                return this.NotFound( );
            }

            this.ViewData["TicketId"] = new SelectList( this.context.Tickets, "Id", "Description", notification.TicketId );
            this.ViewData["UserId"]   = new SelectList( this.context.Users,   "Id", "Id",          notification.UserId );

            return View( notification );
        }

        // POST: Notifications/Edit/5
        // To protect from over-posting attacks, enable the specific properties you want to bind to, for 
        // more details, see http://go.microsoft.com/fwlink/?LinkId=317598.
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Edit(
            int id,
            [Bind( "Id,TicketId,Description,Created,UserId" )]
            Notification notification )
        {
            if ( id != notification.Id )
            {
                return this.NotFound( );
            }

            if ( this.ModelState.IsValid )
            {
                try
                {
                    this.context.Update( notification );
                    await this.context.SaveChangesAsync( ).ConfigureAwait( false );
                }
                catch ( DbUpdateConcurrencyException )
                {
                    if ( !this.NotificationExists( notification.Id ) )
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

            this.ViewData["TicketId"] = new SelectList( this.context.Tickets, "Id", "Description", notification.TicketId );
            this.ViewData["UserId"]   = new SelectList( this.context.Users,   "Id", "Id",          notification.UserId );

            return View( notification );
        }

        // GET: Notifications/Delete/5
        public async Task<IActionResult> Delete( int? id )
        {
            if ( id == null )
            {
                return this.NotFound( );
            }

            Notification notification = await this.context.Notifications.Include( n => n.Ticket )
                                                  .Include( n => n.Sender )
                                                  .FirstOrDefaultAsync( m => m.Id == id )
                                                  .ConfigureAwait( false );

            if ( notification == null )
            {
                return this.NotFound( );
            }

            return View( notification );
        }

        // POST: Notifications/Delete/5
        [HttpPost]
        [ActionName( "Delete" )]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteConfirmed( int id )
        {
            Notification notification = await this.context.Notifications.FindAsync( id ).ConfigureAwait( false );
            this.context.Notifications.Remove( notification );
            await this.context.SaveChangesAsync( ).ConfigureAwait( false );

            return this.RedirectToAction( nameof( this.Index ) );
        }

        private bool NotificationExists( int id )
        {
            return this.context.Notifications.Any( e => e.Id == id );
        }
    }
}
