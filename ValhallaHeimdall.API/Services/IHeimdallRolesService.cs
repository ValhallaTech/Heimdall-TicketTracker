using System.Collections.Generic;
using System.Threading.Tasks;
using ValhallaHeimdall.BLL.Models;

namespace ValhallaHeimdall.API.Services
{
    public interface IHeimdallRolesService
    {
        public Task<bool> AddUserToRoleAsync( HeimdallUser user, string roleName );

        public Task<bool> IsUserInRoleAsync( HeimdallUser user, string roleName );

        public Task<IEnumerable<string>> ListUserRolesAsync( HeimdallUser user );

        public Task<bool> RemoveUserFromRoleAsync( HeimdallUser user, string roleName );

        public Task<ICollection<HeimdallUser>> UsersInRoleAsync( string roleName );

        public Task<IEnumerable<HeimdallUser>> UsersNotInRoleAsync( string roleName );

        // public Task<ICollection<HeimdallUser>> UsersNotInRole( IdentityRole role );
    }
}
