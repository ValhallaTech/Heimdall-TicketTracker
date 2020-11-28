//using System.Collections.Generic;
//using System.Diagnostics;
//using System.Linq;
//using System.Threading.Tasks;
//using Microsoft.AspNetCore.Authorization;
//using Microsoft.AspNetCore.Identity;
//using Microsoft.AspNetCore.Mvc;
//using Microsoft.EntityFrameworkCore;
//using Microsoft.Extensions.Logging;
//using ValhallaHeimdall.API.Services;
//using ValhallaHeimdall.BLL.Models;
//using ValhallaHeimdall.BLL.Models.ViewModels;
//using ValhallaHeimdall.DAL.Data;

//namespace ValhallaHeimdall.API.Controllers
//{
//    public class HomeController : Controller
//    {
//        private readonly ApplicationDbContext context;

//        private readonly ILogger<HomeController> logger;

//        private readonly UserManager<HeimdallUser> userManager;

//        private readonly HeimdallProjectService projectService;

//        private readonly HeimdallRolesService rolesService;

//        public HomeController(
//            ApplicationDbContext context,
//            ILogger<HomeController> logger,
//            UserManager<HeimdallUser> userManager,
//            HeimdallProjectService projectService,
//            HeimdallRolesService rolesService )
//        {
//            this.context        = context;
//            this.logger         = logger;
//            this.userManager    = userManager;
//            this.projectService = projectService;
//            this.rolesService   = rolesService;
//        }

//        public IActionResult Temp( ) => this.View( );

//        public IActionResult FourOhFour( ) => this.View( );

//        [Authorize]
//        public async Task<IActionResult> Index( )
//        {
//            HeimdallUser user = await this.userManager.GetUserAsync( User ).ConfigureAwait( false );
//            PmHomeViewModel          vm   = new PmHomeViewModel( );
//            List<Ticket> tickets = await this.context.Tickets.Include( t => t.DeveloperUser )
//                                             .Include( t => t.OwnerUser )
//                                             .Include( t => t.Project )
//                                             .Include( t => t.TicketPriority )
//                                             .Include( t => t.TicketStatus )
//                                             .Include( t => t.TicketType )
//                                             .Include( t => t.Comments )
//                                             .ThenInclude( tc => tc.User )
//                                             .Include( t => t.Attachments )
//                                             .Include( t => t.Notifications )
//                                             .Include( t => t.Histories )
//                                             .ThenInclude( h => h.User )
//                                             .ToListAsync( )
//                                             .ConfigureAwait( false );
//            vm.NumTickets     = tickets.Count;
//            vm.NumCritical    = tickets.Where( t => t.TicketPriority.Name == "Critical" ).ToList( ).Count;
//            vm.NumOpen        = tickets.Where( t => t.TicketStatus.Name   == "Opened" ).ToList( ).Count;
//            vm.NumUnassigned  = tickets.Where( t => t.DeveloperUserId     == null ).ToList( ).Count;
//            vm.UsersOnProject = await this.context.Users.ToListAsync( ).ConfigureAwait( false );
//            List<ICollection<Notification>> notifications = tickets.ConvertAll( ticket => ticket.Notifications );

//            vm.Notifications = notifications.SelectMany( n => n )
//                                            .Where( n => n.RecipientId == user.Id )
//                                            .Where( n => n.Viewed      == false )
//                                            .ToList( );

//            // give pm personalized data
//            if ( await this.userManager.IsInRoleAsync( user, "ProjectManager" ).ConfigureAwait(false) )
//            {
//                // all pm projects
//                ICollection<Project> projects = await this.projectService.ListUserProjectsAsync( user.Id ).ConfigureAwait( false );

//                // all users on all projects --> list of lists
//                List<ICollection<HeimdallUser>>             users     = new List<ICollection<HeimdallUser>>( );
//                List<List<Ticket>> ticketSet = new List<List<Ticket>>( );

//                foreach ( Project project in projects )
//                {
//                    users.Add( await this.projectService.UsersOnProjectAsync( project.Id ).ConfigureAwait( false ) );
//                    ticketSet.Add( tickets.Where( t => t.Project.Id == project.Id ).ToList( ) );
//                }

//                // flatten list of lists
//                tickets           = ticketSet.SelectMany( t => t ).ToList( );
//                vm.UsersOnProject = users.SelectMany( u => u ).Distinct( ).ToList( );

//                // remove users that are not developers
//                List<HeimdallUser> developer = new List<HeimdallUser>( );

//                foreach ( HeimdallUser flatUser in vm.UsersOnProject )
//                {
//                    if ( await this.rolesService.IsUserInRoleAsync( flatUser, "Developer" ).ConfigureAwait( false ) )
//                    {
//                        developer.Add( flatUser );
//                    }
//                }

//                // reassign view model properties if you're a pm
//                vm.NumTickets    = ticketSet.SelectMany( t => t ).ToList( ).Count;
//                vm.NumCritical   = tickets.Where( t => t.TicketPriority.Name == "Critical" ).ToList( ).Count;
//                vm.NumUnassigned = tickets.Where( t => t.DeveloperUserId     == null ).ToList( ).Count;
//                vm.NumOpen       = tickets.Where( t => t.TicketStatus.Name   == "Opened" ).ToList( ).Count;

//                // if we have developers make suggestion
//                if ( developer.Count > 0 )
//                {
//                    // maximum suggestions
//                    const int Max = 5;

//                    // minimum suggestions = number of tickets
//                    int min = tickets.Where( t => t.DeveloperUserId == null ).ToList( ).Count;

//                    // if it's less than max
//                    int num = min < Max ? min : Max;

//                    for ( int i = 0; i < num; i++ )
//                    {
//                        // get ticket
//                        Ticket ticket = tickets.Where( t => t.DeveloperUserId == null )
//                                               .OrderBy( t => t.TicketPriorityId )
//                                               .ThenBy( t => t.TicketStatusId )
//                                               .Skip( i )
//                                               .Take( 1 )
//                                               .ToList( )[0];
//                        vm.Tickets.Add( ticket );

//                        // get dev
//                        developer = this.projectService.SortListOfDevsByTicketCountAsync( developer, tickets );

//                        // var dev = devs.Count > i ? devs[i] : devs[0];
//                        vm.Developers.Add( developer[0] );

//                        // get task count
//                        vm.Count.Add( tickets.Where( t => t.DeveloperUserId == developer[0].Id ).ToList( ).Count );
//                    }
//                }
//            }

//            // give developer personalized data
//            if ( await this.userManager.IsInRoleAsync( user, "Developer" ).ConfigureAwait( false ) )
//            {
//                vm.Tickets = tickets;

//                ICollection<Project>      projects  = await this.projectService.ListUserProjectsAsync( user.Id ).ConfigureAwait( false );
//                List<List<Ticket>> ticketSet = projects.Select( project => tickets.Where( t => t.Project.Id == project.Id ).ToList( ) ).ToList( );

//                vm.TicketsOnDevProjects    = ticketSet.SelectMany( t => t ).ToList( );
//                vm.TicketsAssignedToDev = vm.TicketsOnDevProjects.Where( t => t.DeveloperUserId == user.Id ).ToList( );
//            }

//            // give submitter personalized data
//            if ( await this.userManager.IsInRoleAsync( user, "Submitter" ).ConfigureAwait( false ) )
//            {
//                vm.Tickets            = tickets;
//                vm.TicketsCreatedByMe = tickets.Where( t => t.OwnerUserId == user.Id ).ToList( );
//            }

//            // give submitter personalized data
//            if ( await this.userManager.IsInRoleAsync( user, "NewUser" ).ConfigureAwait( false ) )
//            {
//                vm.Tickets = tickets;
//            }

//            return this.View( vm );
//        }

//        public IActionResult Privacy( ) => this.View( );

//        public IActionResult LandingPage( ) => this.View( );

//        public HomeController( ILogger<HomeController> logger ) => this.logger = logger;

//        [ResponseCache( Duration = 0, Location = ResponseCacheLocation.None, NoStore = true )]
//        public IActionResult Error( ) =>
//            this.View( new ErrorViewModel { RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier } );
//    }
//}
