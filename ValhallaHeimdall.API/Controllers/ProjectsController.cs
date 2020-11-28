using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.EntityFrameworkCore;
using ValhallaHeimdall.API.Services;
using ValhallaHeimdall.BLL.Models;
using ValhallaHeimdall.BLL.Models.ViewModels;
using ValhallaHeimdall.DAL.Data;

namespace ValhallaHeimdall.API.Controllers
{
    public class ProjectsController : Controller
    {
        private readonly ApplicationDbContext context;

        private readonly IHeimdallProjectService heimdallProjectService;

        private readonly UserManager<HeimdallUser> userManager;

        private readonly IHeimdallAccessService accessService;

        private readonly IHeimdallProjectService projectService;

        private readonly IHeimdallRolesService rolesService;

        public ProjectsController(
            ApplicationDbContext      context,
            IHeimdallProjectService   projectService,
            UserManager<HeimdallUser> userManager,
            IHeimdallAccessService    accessService,
            IHeimdallRolesService     rolesService )
        {
            this.context                = context;
            this.projectService         = projectService;
            this.heimdallProjectService = projectService;
            this.userManager            = userManager;
            this.accessService          = accessService;
            this.rolesService           = rolesService;
        }

        // GET: Projects Index
        public async Task<IActionResult> Index( ) =>
            this.View( await this.context.Projects.ToListAsync( ).ConfigureAwait( false ) );

        // GET: Projects/ MyProjects
        public async Task<IActionResult> CurrentUserProjects( int? id )
        {
            // Should function similarly to MyTickets, but able to use service to filter projects seen based on user's role or if
            // the user submitted a ticket for that project.
            List<Project> model  = new List<Project>( );
            string userId = this.userManager.GetUserId( this.User );

            if ( this.User.IsInRole( "Administrator" ) )
            {
                // create or use a service (HeimdallProjectService?) that will filter a user's projects on the MyProjects View
                model = this.context.Projects.ToList( );

                return this.View( "CurrentUserProjects", model );
            }

            if ( this.User.IsInRole( "ProjectManager" )
              || this.User.IsInRole( "Developer" )
              || this.User.IsInRole( "Submitter" )
              || this.User.IsInRole( "NewUser" ) )
            {
                model =
                    await this.projectService.ListUserProjectsAsync( userId ).ConfigureAwait( false ) as List<Project>;

                return this.View( "CurrentUserProjects", model );
            }

            return this.NotFound( );
        }

        // GET: Projects/Create
        [Authorize( Roles = "Administrator, ProjectManager" )]
        public IActionResult Create( ) => this.View( );

        // POST: Projects/Create
        // To protect from overposting attacks, enable the specific properties you want to bind to, for
        // more details, see http://go.microsoft.com/fwlink/?LinkId=317598.
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Create(
            [Bind( "Id,Name,ImagePath,ImageData" )]
            Project project )
        {
            if ( !this.User.IsInRole( "Demo" ) )
            {
                if ( this.ModelState.IsValid )
                {
                    this.context.Add( project );
                    await this.context.SaveChangesAsync( ).ConfigureAwait( false );

                    return this.RedirectToAction( nameof( this.Index ) );
                }

                return this.View( project );
            }
            else
            {
                this.TempData["DemoLockout"] =
                    "Your changes have not been saved. To make changes to the database, please log in as a full user.";

                return this.RedirectToAction( "CurrentUserProjects", "Projects" );
            }
        }

        // GET: Projects/Details/5
        public async Task<IActionResult> Details( int? id )
        {
            if ( id == null )
            {
                return this.NotFound( );
            }

            Project project = await this.context.Projects
                                        .Include(
                                                 p => p
                                                     .ProjectUsers ) // in Addition to project, bring the reference to project user
                                        .ThenInclude( p => p.User ) // also bring the user reference
                                        .FirstOrDefaultAsync( m => m.Id == id )
                                        .ConfigureAwait(
                                                        false ); // go into db, go into projects table, find the first project with this id, grab that and only that item with that id

            project.Tickets = await this.context.Tickets.Where( t => t.ProjectId == id )
                                        .Include( t => t.DeveloperUser )
                                        .Include( t => t.OwnerUser )
                                        .Include( t => t.Project )
                                        .Include( t => t.TicketPriority )
                                        .Include( t => t.TicketStatus )
                                        .Include( t => t.TicketType )
                                        .ToListAsync( )
                                        .ConfigureAwait( false );

            return this.View( project );
        }

        // GET: Projects/Edit
        [Authorize( Roles = "Administrator, ProjectManager" )]
        public async Task<IActionResult> Edit( int? id )
        {
            if ( id == null )
            {
                return this.NotFound( );
            }

            Project project = await this.context.Projects.FindAsync( id ).ConfigureAwait( false );

            if ( project == null )
            {
                return this.NotFound( );
            }

            return this.View( project );
        }

        // POST: Projects/Edit/5
        // To protect from overposting attacks, enable the specific properties you want to bind to, for
        // more details, see http://go.microsoft.com/fwlink/?LinkId=317598.
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Edit(
            int id,
            [Bind( "Id,Name,ImagePath,ImageData" )]
            Project project )
        {
            if ( !this.User.IsInRole( "DemoUser" ) )
            {
                if ( id != project.Id )
                {
                    return this.NotFound( );
                }

                if ( this.ModelState.IsValid )
                {
                    try
                    {
                        this.context.Update( project );
                        await this.context.SaveChangesAsync( ).ConfigureAwait( false );
                    }
                    catch ( DbUpdateConcurrencyException )
                    {
                        if ( !this.ProjectExists( project.Id ) )
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

                return this.View( project );
            }
            else
            {
                this.TempData["DemoLockout"] =
                    "Your changes have not been saved. To make changes to the database, please log in as a full user.";

                return this.RedirectToAction( "CurrentUserProjects", "Projects" );
            }
        }

        // private bool ProjectExists( int id ) => throw new NotImplementedException( );

        // GET: Projects/Delete/5
        public async Task<IActionResult> Delete( int? id )
        {
            if ( id == null )
            {
                return this.NotFound( );
            }

            Project project =
                await this.context.Projects.FirstOrDefaultAsync( m => m.Id == id ).ConfigureAwait( false );

            if ( project == null )
            {
                return this.NotFound( );
            }

            return this.View( project );
        }

        // POST: Projects/Delete/5
        [HttpPost]
        [ActionName( "Delete" )]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteConfirmed( int id )
        {
            Project project = await this.context.Projects.FindAsync( id ).ConfigureAwait( false );
            this.context.Projects.Remove( project );
            await this.context.SaveChangesAsync( ).ConfigureAwait( false );

            return this.RedirectToAction( nameof( this.Index ) );
        }

        private bool ProjectExists( int id )
        {
            return this.context.Projects.Any( pe => pe.Id == id );
        }

        // GET: Projects/ManageProjectUsers
        [Authorize( Roles = "Administrator, ProjectManager" )]
        public async Task<IActionResult> AssignUsers( int id )
        {
            // By default, this is a get method//
            ManageProjectUsersViewModel model   = new ManageProjectUsersViewModel( ); // Newing up an instance of ManageProjectUsersViewModel
            Project project = await this.context.Projects.FindAsync( id ).ConfigureAwait( false );

            model.Project = project;
            List<HeimdallUser> users   = await this.context.Users.ToListAsync( ).ConfigureAwait( false );
            List<HeimdallUser> members = ( List<HeimdallUser> )await this.projectService.UsersOnProjectAsync( id ).ConfigureAwait( false );
            model.Users = new MultiSelectList( users, "Id", "FullName", members );

            return this.View( model );
        }

        // POST: Projects/Assign Users To Project
        [Authorize( Roles = "Administrator, ProjectManager" )]
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> AssignUsers( ManageProjectUsersViewModel model )
        {
            if ( !this.User.IsInRole( "Demo" ) )
            {
                if ( this.ModelState.IsValid )
                {
                    if ( model.SelectedUsers != null )
                    {
                        Project currentMembers = await this.context.Projects.Include( p => p.ProjectUsers )
                                                           .FirstOrDefaultAsync( p => p.Id == model.Project.Id )
                                                           .ConfigureAwait( false );
                        List<string> memberIds = currentMembers.ProjectUsers.Select( u => u.UserId ).ToList( );

                        foreach ( string id in memberIds )
                        {
                            await this.projectService.RemoveUserFromProjectAsync( id, model.Project.Id ).ConfigureAwait( false );
                        }

                        foreach ( string id in model.SelectedUsers )
                        {
                            await this.projectService.AddUserToProjectAsync( id, model.Project.Id ).ConfigureAwait( false );
                        }

                        return this.RedirectToAction( "Details", "Projects", new { id = model.Project.Id } );

                        // return RedirectToAction(name of(BlogPosts), new { id = post.BlogId }); Default statement that returns to all projects: return RedirectToAction("Index", "Projects");
                    }
                    else
                    {
                        Debug.WriteLine( "****ERROR****" );

                        // Send an error message back
                    }
                }

                return this.View( model );
            }
            else
            {
                this.TempData["DemoLockout"] =
                    "Your changes have not been saved. To make changes to the database, please log in as a full user.";

                return this.RedirectToAction( "CurrentUserProjects", "Projects" );
            }
        }
    }
}
