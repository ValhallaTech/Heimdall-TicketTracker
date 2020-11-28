using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using ValhallaHeimdall.BLL.Models;
using ValhallaHeimdall.DAL.Data;

namespace ValhallaHeimdall.API.Services
{
    public class HeimdallProjectService : IHeimdallProjectService
    {
        private readonly ApplicationDbContext context;

        private readonly RoleManager<IdentityRole> roleManager;

        private readonly UserManager<HeimdallUser> userManager;

        public HeimdallProjectService(
            ApplicationDbContext      context,
            RoleManager<IdentityRole> roleManager,
            UserManager<HeimdallUser> userManager )
        {
            this.context     = context;
            this.userManager = userManager;
            this.roleManager = roleManager;
        }

        public async Task<bool> IsUserOnProjectAsync( string userId, int projectId )
        {
            Project project = await this.context.Projects
                                        .Include( u => u.ProjectUsers.Where( u => u.UserId == userId ) )
                                        .ThenInclude( u => u.User )
                                        .FirstOrDefaultAsync( u => u.Id == projectId )
                                        .ConfigureAwait( false );
            bool result = project.ProjectUsers.Any( u => u.UserId == userId );

            return this.context.ProjectUsers.Any( pu => pu.UserId == userId && pu.ProjectId == projectId );
        }

        public async Task<ICollection<Project>> ListUserProjectsAsync( string userId )
        {
            HeimdallUser user = await this.context.Users.Include( p => p.ProjectUsers )
                                          .ThenInclude( p => p.Project )
                                          .FirstOrDefaultAsync( p => p.Id == userId )
                                          .ConfigureAwait( false );

            List<Project> projects = user.ProjectUsers.SelectMany( p => (IEnumerable<Project>)p.Project ).ToList( );

            return projects;
        }

        public async Task AddUserToProjectAsync( string userId, int projectId )
        {
            if ( !await this.IsUserOnProjectAsync( userId, projectId ).ConfigureAwait( false ) )
            {
                try
                {
                    await this.context.ProjectUsers
                              .AddAsync( new ProjectUser { ProjectId = projectId, UserId = userId } )
                              .ConfigureAwait( false );
                    await this.context.SaveChangesAsync( ).ConfigureAwait( false );
                }
                catch ( Exception ex )
                {
                    Debug.WriteLine( $"Error adding user to project. ==> {ex.Message}" );

                    throw;
                }
            }
        }

        public async Task RemoveUserFromProjectAsync( string userId, int projectId )
        {
            if ( await this.IsUserOnProjectAsync( userId, projectId ).ConfigureAwait( false ) )
            {
                try
                {
                    ProjectUser projectUser = new ProjectUser( ) { UserId = userId, ProjectId = projectId };

                    this.context.ProjectUsers.Remove( projectUser );
                    await this.context.SaveChangesAsync( ).ConfigureAwait( false );
                }
                catch ( Exception ex )
                {
                    Debug.WriteLine( $"Error removing user from project. ==> {ex.Message}" );

                    throw;
                }
            }
        }

        public async Task<ICollection<HeimdallUser>> UsersOnProjectAsync( int projectId )
        {
            Project project = await this.context.Projects.Include( u => u.ProjectUsers )
                                        .ThenInclude( u => u.User )
                                        .FirstOrDefaultAsync( u => u.Id == projectId )
                                        .ConfigureAwait( false );
            List<HeimdallUser> projectUsers = project.ProjectUsers.ConvertAll( p => p.User );

            return projectUsers;
        }

        public async Task<ICollection<HeimdallUser>> UsersNotOnProjectAsync( int projectId )
        {
            List<HeimdallUser>        users01 = await this.context.Users.ToListAsync( ).ConfigureAwait( false );
            ICollection<HeimdallUser> users02 = new List<HeimdallUser>( );

            foreach ( HeimdallUser user in users01 )
            {
                bool result = await this.IsUserOnProjectAsync( user.Id, projectId ).ConfigureAwait( false );

                if ( result == false )
                {
                    users01.Add( user );
                }
            }

            return users02;
        }

        public List<HeimdallUser> SortListOfDevsByTicketCountAsync(
            List<HeimdallUser>          users,
            List<Ticket> tickets )
        {
            int i, j;
            int n = users.Count;

            for ( j = n; j > 0; j-- )
            {
                for ( i = 0; i < j; i++ )
                {
                    List<ProjectUser> pu1 = users[i].ProjectUsers;
                    int    tc1 = pu1.Sum( pu => tickets.Where( t => t.DeveloperUserId == users[i].Id ).ToList( ).Count );

                    List<ProjectUser> pu2 = users[i + 1].ProjectUsers;
                    int tc2 = pu2.Sum(
                                      pu => tickets.Where( t => t.DeveloperUserId == users[i + 1].Id )
                                                   .ToList( )
                                                   .Count );

                    if ( tc2 >= tc1 )
                    {
                        continue;
                    }

                    HeimdallUser temp = users[i];
                    users[i] = users[i + 1];
                    users[i            + 1] = temp;
                }
            }

            return users;
        }
    }
}

// public async Task<ICollection<HeimdallUser>> UsersNotOnProjectAsync(int projectId)
// {
// return await this.context.Users.Where(u => IsUserOnProjectAsync(u.Id, projectId).Result == false).ToListAsync();
// }

// Task<ICollection<DbContext>> IPSProjectService.UsersOnProject(int projectId)
// {
// throw new NotImplementedException();
// }

// Task<ICollection<DbContext>> IPSProjectService.UsersNotOnProject(int projectId)
// {
// throw new NotImplementedException();
// }

// public async Task<ICollection<HeimdallUser>> UsersNotOnProject( int projectId )
// {
// return await this.context.Users.Where( u => await this.IsUserOnProject( u.Id, projectId ).ConfigureAwait(false) == false ).ToListAsync().ConfigureAwait(false);
// }
