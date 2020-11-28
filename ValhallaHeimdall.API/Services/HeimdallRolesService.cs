using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using ValhallaHeimdall.BLL.Models;

namespace ValhallaHeimdall.API.Services
{
    public class HeimdallRolesService : IHeimdallRolesService
    {
        private readonly RoleManager<IdentityRole> roleManager;

        private readonly UserManager<HeimdallUser> userManager;

        public HeimdallRolesService( RoleManager<IdentityRole> roleManager, UserManager<HeimdallUser> userManager )
        {
            this.roleManager = roleManager;
            this.userManager = userManager;
        }

        public async Task<bool> AddUserToRoleAsync( HeimdallUser user, string roleName )
        {
            IdentityResult result = await this.userManager.AddToRoleAsync( user, roleName ).ConfigureAwait( false );

            return result.Succeeded;
        }

        public Task<bool> IsUserInRoleAsync( HeimdallUser user, string roleName ) => this.userManager.IsInRoleAsync( user, roleName );

        public async Task<IEnumerable<string>> ListUserRolesAsync( HeimdallUser user ) => await this.userManager.GetRolesAsync( user ).ConfigureAwait( false );

        public async Task<bool> RemoveUserFromRoleAsync( HeimdallUser user, string roleName )
        {
            IdentityResult result = await this.userManager.RemoveFromRoleAsync( user, roleName ).ConfigureAwait( false );

            return result.Succeeded;
        }

        public async Task<ICollection<HeimdallUser>> UsersInRoleAsync( string roleName )
        {
            IList<HeimdallUser> users = await this.userManager.GetUsersInRoleAsync( roleName ).ConfigureAwait( false );

            return users;
        }

        public async Task<IEnumerable<HeimdallUser>> UsersNotInRoleAsync( string roleName )
        {
            IList<HeimdallUser> inRole = await this.userManager.GetUsersInRoleAsync( roleName ).ConfigureAwait( false );
            List<HeimdallUser>  users  = await this.userManager.Users.ToListAsync( ).ConfigureAwait( false );

            return users.Except( inRole );
        }

        // public Task<ICollection<HeimdallUser>> UsersNotInRole( IdentityRole role )
        // {
        // ////var roleId = await this.roleManager.GetRoleIdAsync( role ).ConfigureAwait( false );

        // return this.userManager.Users
        // .Where( u => IsUserInRoleAsync( u, role.Name ).Result == false )
        // .ToListAsync( )
        // .ConfigureAwait( false );
        // }
    }
}
