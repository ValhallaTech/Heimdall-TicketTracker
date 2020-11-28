using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using ValhallaHeimdall.DAL.Data;

namespace ValhallaHeimdall.API.Services
{
    public class HeimdallAccessService : IHeimdallAccessService
    {
        private readonly ApplicationDbContext context;

        public HeimdallAccessService( ApplicationDbContext context ) => this.context = context;

        public async Task<bool> CanInteractProjectAsync( string userId, int projectId, string roleName )
        {
            switch ( roleName )
            {
                case "Administrator":
                    return true;

                case "ProjectManager":

                    return await this.context.ProjectUsers
                                     .Where( pu => pu.UserId == userId && pu.ProjectId == projectId )
                                     .AnyAsync( )
                                     .ConfigureAwait( false );

                default:
                    return false;
            }
        }

        public async Task<bool> CanInteractTicketAsync( string userId, int ticketId, string roleName )
        {
            bool result = false;

            switch ( roleName )
            {
                case "Administrator":
                    result = true;

                    break;

                case "ProjectManager":
                    int projectId = ( await this.context.Tickets
                                                .FindAsync( ticketId )
                                                .ConfigureAwait( false ) )
                        .ProjectId;

                    if ( !await this.context.ProjectUsers
                                    .Where( pu => pu.UserId == userId && pu.ProjectId == ticketId )
                                    .AnyAsync( )
                                    .ConfigureAwait( false ) )
                    {
                        result = false;
                    }

                    result = true;

                    break;

                case "Developer":
                    if ( await this.context.Tickets
                                   .Where( t => t.DeveloperUserId == userId && t.Id == ticketId )
                                   .AnyAsync( )
                                   .ConfigureAwait( false ) )
                    {
                        result = true;
                    }

                    break;

                case "Submitter":
                    if ( await this.context.Tickets
                                   .Where( t => t.OwnerUserId == userId && t.Id == ticketId )
                                   .AnyAsync( )
                                   .ConfigureAwait( false ) )
                    {
                        result = true;
                    }

                    break;
            }

            return false;
        }
    }
}
