using System.Collections.Generic;
using System.Threading.Tasks;
using ValhallaHeimdall.BLL.Models;

namespace ValhallaHeimdall.API.Services
{
    public interface IHeimdallProjectService
    {
        public Task<bool> IsUserOnProjectAsync( string userId, int projectId );

        public Task<ICollection<Project>> ListUserProjectsAsync( string userId );

        public Task AddUserToProjectAsync( string userId, int projectId );

        public Task RemoveUserFromProjectAsync( string userId, int projectId );

        public Task<ICollection<HeimdallUser>> UsersOnProjectAsync( int projectId );

        public Task<ICollection<HeimdallUser>> UsersNotOnProjectAsync( int projectId );

        public List<HeimdallUser> SortListOfDevsByTicketCountAsync( List<HeimdallUser> users, List<Ticket> tickets );
    }
}
