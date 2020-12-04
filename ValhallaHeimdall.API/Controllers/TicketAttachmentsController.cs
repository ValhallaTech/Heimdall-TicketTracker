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
    public class TicketAttachmentsController : Controller
    {
        private readonly ApplicationDbContext context;

        private readonly UserManager<HeimdallUser> userManager;

        public TicketAttachmentsController( ApplicationDbContext context, UserManager<HeimdallUser> userManager )
        {
            this.context     = context;
            this.userManager = userManager;
        }

        // GET: TicketAttachments
        public async Task<IActionResult> Index( )
        {
            IIncludableQueryable<TicketAttachment, HeimdallUser> applicationDbContext = this.context.TicketAttachments
                                                                                            .Include( t => t.Ticket )
                                                                                            .Include( t => t.User );

            return this.View( await applicationDbContext.ToListAsync( ).ConfigureAwait( false ) );
        }

        // GET: TicketAttachments/Details/5
        public async Task<IActionResult> Details( int? id )
        {
            if ( id == null )
            {
                return this.NotFound( );
            }

            TicketAttachment ticketAttachment = await this.context.TicketAttachments
                                                          .Include( t => t.Ticket )
                                                          .Include( t => t.User )
                                                          .FirstOrDefaultAsync( m => m.Id == id )
                                                          .ConfigureAwait( false );

            if ( ticketAttachment == null )
            {
                return this.NotFound( );
            }

            return this.View( ticketAttachment );
        }

        // GET: TicketAttachments/Create
        public IActionResult Create( )
        {
            this.ViewData["TicketId"] = new SelectList( this.context.Tickets, "Id", "Description" );
            this.ViewData["UserId"]   = new SelectList( this.context.Users,   "Id", "Id" );

            return this.View( );
        }

        // POST: TicketAttachments/Create
        // To protect from overposting attacks, enable the specific properties you want to bind to, for
        // more details, see http://go.microsoft.com/fwlink/?LinkId=317598.
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Create( [Bind( "Id,FilePath,FileData,Description,Created,TicketId,UserId" )]
                                                 TicketAttachment ticketAttachment )
        {
            if ( this.ModelState.IsValid )
            {
                await context.AddAsync( ticketAttachment ).ConfigureAwait( false );

                try
                {
                    await this.context.SaveChangesAsync( ).ConfigureAwait( false );
                }
                catch ( DbUpdateException updateException )
                {
                    // TODO: Handle the Microsoft.EntityFrameworkCore.DbUpdateException
                }

                return this.RedirectToAction( nameof( this.Index ) );
            }

            this.ViewData["TicketId"] =
                new SelectList( this.context.Tickets, "Id", "Description", ticketAttachment.TicketId );
            this.ViewData["UserId"] = new SelectList( this.context.Users, "Id", "Id", ticketAttachment.UserId );

            return this.View( ticketAttachment );
        }

        // GET: TicketAttachments/Edit/5
        public async Task<IActionResult> Edit( int? id )
        {
            if ( id == null )
            {
                return this.NotFound( );
            }

            TicketAttachment ticketAttachment = await this.context.TicketAttachments.FindAsync( id ).ConfigureAwait( false );

            if ( ticketAttachment == null )
            {
                return this.NotFound( );
            }

            this.ViewData["TicketId"] =
                new SelectList( this.context.Tickets, "Id", "Description", ticketAttachment.TicketId );
            this.ViewData["UserId"] = new SelectList( this.context.Users, "Id", "Id", ticketAttachment.UserId );

            return this.View( ticketAttachment );
        }

        // POST: TicketAttachments/Edit/5
        // To protect from overposting attacks, enable the specific properties you want to bind to, for
        // more details, see http://go.microsoft.com/fwlink/?LinkId=317598.
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Edit( int id,
                                               [Bind( "Id,FilePath,FileData,Description,Created,TicketId,UserId" )]
                                               TicketAttachment ticketAttachment )
        {
            if ( id != ticketAttachment.Id )
            {
                return this.NotFound( );
            }

            if ( this.ModelState.IsValid )
            {
                try
                {
                    this.context.Update( ticketAttachment );
                    await this.context.SaveChangesAsync( ).ConfigureAwait( false );
                }
                catch ( DbUpdateConcurrencyException ) when ( !this.TicketAttachmentExists( ticketAttachment.Id ) )
                {
                    return this.NotFound( );
                }

                return this.RedirectToAction( nameof( this.Index ) );
            }

            this.ViewData["TicketId"] =
                new SelectList( this.context.Tickets, "Id", "Description", ticketAttachment.TicketId );
            this.ViewData["UserId"] = new SelectList( this.context.Users, "Id", "Id", ticketAttachment.UserId );

            return this.View( ticketAttachment );
        }

        // GET: TicketAttachments/Delete/5
        public async Task<IActionResult> Delete( int? id )
        {
            if ( id == null )
            {
                return this.NotFound( );
            }

            TicketAttachment ticketAttachment = await this.context.TicketAttachments
                                                          .Include( t => t.Ticket )
                                                          .Include( t => t.User )
                                                          .FirstOrDefaultAsync( m => m.Id == id )
                                                          .ConfigureAwait( false );

            if ( ticketAttachment == null )
            {
                return this.NotFound( );
            }

            return this.View( ticketAttachment );
        }

        // POST: TicketAttachments/Delete/5
        [HttpPost]
        [ActionName( "Delete" )]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteConfirmed( int id )
        {
            TicketAttachment ticketAttachment = await this.context.TicketAttachments.FindAsync( id ).ConfigureAwait( false );
            this.context.TicketAttachments.Remove( ticketAttachment );
            await this.context.SaveChangesAsync( ).ConfigureAwait( false );

            return this.RedirectToAction( nameof( this.Index ) );
        }

        private bool TicketAttachmentExists( int id )
        {
            return this.context.TicketAttachments.Any( e => e.Id == id );
        }
    }
}
