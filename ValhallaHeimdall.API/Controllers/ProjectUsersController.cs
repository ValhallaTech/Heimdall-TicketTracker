using System.Linq;
using System.Threading.Tasks;
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
    public class ProjectUsersController : Controller
    {
        private readonly ApplicationDbContext context;

        private readonly IHeimdallProjectService projectService;

        private readonly UserManager<HeimdallUser> userManager;

        public ProjectUsersController(
            ApplicationDbContext      context,
            IHeimdallProjectService   projectService,
            UserManager<HeimdallUser> userManager )
        {
            this.context        = context;
            this.projectService = projectService;
            this.userManager    = userManager;
        }

        // GET: ProjectUsers
        public async Task<IActionResult> Index( )
        {
            IIncludableQueryable<ProjectUser, HeimdallUser> applicationDbContext =
                this.context.ProjectUsers.Include( p => p.Project ).Include( p => p.User );

            return this.View( await applicationDbContext.ToListAsync( ).ConfigureAwait( false ) );
        }

        // GET: ProjectUsers/Details/5
        public async Task<IActionResult> Details( int? id )
        {
            if ( id == null )
            {
                return this.NotFound( );
            }

            ProjectUser projectUser = await this.context.ProjectUsers.Include( p => p.Project )
                                                .Include( p => p.User )
                                                .FirstOrDefaultAsync( m => m.ProjectId == id )
                                                .ConfigureAwait( false );

            if ( projectUser == null )
            {
                return this.NotFound( );
            }

            return this.View( projectUser );
        }

        // GET: ProjectUsers/Create
        public IActionResult Create( )
        {
            this.ViewData["ProjectId"] = new SelectList( this.context.Projects,      "Id", "Name" );
            this.ViewData["UserId"]    = new SelectList( this.context.HeimdallUsers, "Id", "Id" );

            return this.View( );
        }

        // POST: ProjectUsers/Create
        // To protect from overposting attacks, enable the specific properties you want to bind to, for
        // more details, see http://go.microsoft.com/fwlink/?LinkId=317598.
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Create(
            [Bind( "UserId,ProjectId,AddProjectUsers,RemoveProjectUsers" )]
            ProjectUser projectUser )
        {
            if ( this.ModelState.IsValid )
            {
                this.context.Add( projectUser );
                await this.context.SaveChangesAsync( ).ConfigureAwait( false );

                return this.RedirectToAction( nameof( this.Index ) );
            }

            this.ViewData["ProjectId"] = new SelectList( this.context.Projects, "Id", "Name", projectUser.ProjectId );
            this.ViewData["UserId"]    = new SelectList( this.context.HeimdallUsers, "Id", "Id", projectUser.UserId );

            return this.View( projectUser );
        }

        // GET: ProjectUsers/Edit/5
        public async Task<IActionResult> Edit( int? id )
        {
            if ( id == null )
            {
                return this.NotFound( );
            }

            ProjectUser projectUser = await this.context.ProjectUsers.FindAsync( id ).ConfigureAwait( false );

            if ( projectUser == null )
            {
                return this.NotFound( );
            }

            this.ViewData["ProjectId"] = new SelectList( this.context.Projects, "Id", "Name", projectUser.ProjectId );
            this.ViewData["UserId"]    = new SelectList( this.context.HeimdallUsers, "Id", "Id", projectUser.UserId );

            return this.View( projectUser );
        }

        // POST: ProjectUsers/Edit/5
        // To protect from overposting attacks, enable the specific properties you want to bind to, for
        // more details, see http://go.microsoft.com/fwlink/?LinkId=317598.
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Edit(
            int id,
            [Bind( "UserId,ProjectId,AddProjectUsers,RemoveProjectUsers" )]
            ProjectUser projectUser )
        {
            if ( id != projectUser.ProjectId )
            {
                return this.NotFound( );
            }

            if ( this.ModelState.IsValid )
            {
                try
                {
                    this.context.Update( projectUser );
                    await this.context.SaveChangesAsync( ).ConfigureAwait( false );
                }
                catch ( DbUpdateConcurrencyException ) when ( !this.ProjectUserExists( projectUser.ProjectId ) )
                {
                    return this.NotFound( );
                }

                return this.RedirectToAction( nameof( this.Index ) );
            }

            this.ViewData["ProjectId"] = new SelectList( this.context.Projects, "Id", "Name", projectUser.ProjectId );
            this.ViewData["UserId"]    = new SelectList( this.context.HeimdallUsers, "Id", "Id", projectUser.UserId );

            return this.View( projectUser );
        }

        // GET: ProjectUsers/Delete/5
        public async Task<IActionResult> Delete( int? id )
        {
            if ( id == null )
            {
                return this.NotFound( );
            }

            ProjectUser projectUser = await this.context.ProjectUsers.Include( p => p.Project )
                                                .Include( p => p.User )
                                                .FirstOrDefaultAsync( m => m.ProjectId == id )
                                                .ConfigureAwait( false );

            if ( projectUser == null )
            {
                return this.NotFound( );
            }

            return this.View( projectUser );
        }

        // POST: ProjectUsers/Delete/5
        [HttpPost, ActionName( "Delete" )]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteConfirmed( int id )
        {
            ProjectUser projectUser = await this.context.ProjectUsers.FindAsync( id ).ConfigureAwait( false );
            this.context.ProjectUsers.Remove( projectUser );
            await this.context.SaveChangesAsync( ).ConfigureAwait( false );

            return this.RedirectToAction( nameof( this.Index ) );
        }

        private bool ProjectUserExists( int id )
        {
            return this.context.ProjectUsers.Any( e => e.ProjectId == id );
        }
    }
}
