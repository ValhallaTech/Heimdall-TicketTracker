using System;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using ValhallaHeimdall.BLL.Models;

namespace ValhallaHeimdall.DAL.Data
{
    public enum Roles
    {
        Administrator,

        ProjectManager,

        Developer,

        Submitter,

        NewUser,

        DemoUser
    }

    public enum TicketTypes
    {
        BasicFunctionality,

        UserInterface,

        FeatureEnhancement,

        DocumentationUpdate,

        Bug
    }

    public enum TicketPriorities
    {
        Low,

        Moderate,

        Major,

        Critical
    }

    public enum TicketStatuses
    {
        Opened,

        InProgress,

        QualityAssurance,

        FinalPass,

        Closed
    }

    public static class ContextSeed
    {
        public static async Task RunSeedMethodsAsync(
            ApplicationDbContext      context,
            RoleManager<IdentityRole> roleManager,
            UserManager<HeimdallUser> userManager )
        {
            await SeedRolesAsync( roleManager ).ConfigureAwait( false );
            await SeedDefaultUsersAsync( userManager ).ConfigureAwait( false );
            await SeedTicketTypesAsync( context ).ConfigureAwait( false );
            await SeedTicketStatusesAsync( context ).ConfigureAwait( false );
            await SeedTicketPrioritiesAsync( context ).ConfigureAwait( false );
            await SeedProjectsAsync( context ).ConfigureAwait( false );
            await SeedProjectUsersAsync( context, userManager ).ConfigureAwait( false );
            await SeedTicketsAsync( context, userManager ).ConfigureAwait( false );
        }

        private static async Task SeedRolesAsync( RoleManager<IdentityRole> roleManager )
        {
            await roleManager.CreateAsync( new IdentityRole( nameof( Roles.Administrator ) ) ).ConfigureAwait( false );
            await roleManager.CreateAsync( new IdentityRole( nameof( Roles.ProjectManager ) ) ).ConfigureAwait( false );
            await roleManager.CreateAsync( new IdentityRole( nameof( Roles.Developer ) ) ).ConfigureAwait( false );
            await roleManager.CreateAsync( new IdentityRole( nameof( Roles.Submitter ) ) ).ConfigureAwait( false );
            await roleManager.CreateAsync( new IdentityRole( nameof( Roles.NewUser ) ) ).ConfigureAwait( false );
            await roleManager.CreateAsync( new IdentityRole( nameof( Roles.DemoUser ) ) ).ConfigureAwait( false );
        }

        private static async Task SeedDefaultUsersAsync( UserManager<HeimdallUser> userManager )
        {
            HeimdallUser defaultUser = new HeimdallUser
                                       {
                                           UserName       = "testmail01@mailinator.com",
                                           Email          = "testmail01@mailinator.com",
                                           FirstName      = "Fred",
                                           LastName       = "Smith",
                                           EmailConfirmed = true
                                       };

            try
            {
                HeimdallUser user = await userManager.FindByEmailAsync( defaultUser.Email ).ConfigureAwait( false );

                if ( user == null )
                {
                    await userManager.CreateAsync( defaultUser, "Abc&123!" ).ConfigureAwait( false );
                    await userManager.AddToRoleAsync( defaultUser, nameof( Roles.Administrator ) )
                                     .ConfigureAwait( false );
                }
            }
            catch ( Exception ex )
            {
                Debug.WriteLine( "************** ERROR *************" );
                Debug.WriteLine( "Error Seeding Default Admin User." );
                Debug.WriteLine( ex.Message );
                Debug.WriteLine( "**********************************" );

                throw;
            }



            defaultUser = new HeimdallUser
                          {
                              UserName       = "testmail02@mailinator.com",
                              Email          = "testmail02@mailinator.com",
                              FirstName      = "Bill",
                              LastName       = "Williams",
                              EmailConfirmed = true
                          };

            try
            {
                HeimdallUser user = await userManager.FindByEmailAsync( defaultUser.Email ).ConfigureAwait( false );

                if ( user == null )
                {
                    await userManager.CreateAsync( defaultUser, "Abc&123!" ).ConfigureAwait( false );
                    await userManager.AddToRoleAsync( defaultUser, nameof( Roles.ProjectManager ) )
                                     .ConfigureAwait( false );
                }
            }
            catch ( Exception ex )
            {
                Debug.WriteLine( "************** ERROR *************" );
                Debug.WriteLine( "Error Seeding Default Project Manager User." );
                Debug.WriteLine( ex.Message );
                Debug.WriteLine( "**********************************" );

                throw;
            }



            #region Developer Seed

            defaultUser = new HeimdallUser
                          {
                              UserName       = "testmail03@mailinator.com",
                              Email          = "testmail03@mailinator.com",
                              FirstName      = "Nugz",
                              LastName       = "McNugz",
                              EmailConfirmed = true
                          };

            try
            {
                HeimdallUser user = await userManager.FindByEmailAsync( defaultUser.Email ).ConfigureAwait( false );

                if ( user == null )
                {
                    await userManager.CreateAsync( defaultUser, "Abc&123!" ).ConfigureAwait( false );
                    await userManager.AddToRoleAsync( defaultUser, nameof( Roles.Developer ) ).ConfigureAwait( false );
                }
            }
            catch ( Exception ex )
            {
                Debug.WriteLine( "************** ERROR *************" );
                Debug.WriteLine( "Error Seeding Default Developer User." );
                Debug.WriteLine( ex.Message );
                Debug.WriteLine( "**********************************" );

                throw;
            }

            #endregion

            #region Submitter Seed

            defaultUser = new HeimdallUser
                          {
                              UserName       = "testmail04@mailinator.com",
                              Email          = "testmail04@mailinator.com",
                              FirstName      = "Nil",
                              LastName       = "Nullable",
                              EmailConfirmed = true
                          };

            try
            {
                HeimdallUser user = await userManager.FindByEmailAsync( defaultUser.Email ).ConfigureAwait( false );

                if ( user == null )
                {
                    await userManager.CreateAsync( defaultUser, "Abc&123!" ).ConfigureAwait( false );
                    await userManager.AddToRoleAsync( defaultUser, nameof( Roles.Submitter ) ).ConfigureAwait( false );
                }
            }
            catch ( Exception ex )
            {
                Debug.WriteLine( "************** ERROR *************" );
                Debug.WriteLine( "Error Seeding Default Submitter User." );
                Debug.WriteLine( ex.Message );
                Debug.WriteLine( "**********************************" );

                throw;
            }

            #endregion

            #region New User Seed

            defaultUser = new HeimdallUser
                          {
                              UserName       = "testmail05@mailinator.com",
                              Email          = "testmail05@mailinator.com",
                              FirstName      = "Noob",
                              LastName       = "Neophyte",
                              EmailConfirmed = true
                          };

            try
            {
                HeimdallUser user = await userManager.FindByEmailAsync( defaultUser.Email ).ConfigureAwait( false );

                if ( user == null )
                {
                    await userManager.CreateAsync( defaultUser, "Abc&123!" ).ConfigureAwait( false );
                    await userManager.AddToRoleAsync( defaultUser, nameof( Roles.NewUser ) ).ConfigureAwait( false );
                }
            }
            catch ( Exception ex )
            {
                Debug.WriteLine( "************** ERROR *************" );
                Debug.WriteLine( "Error Seeding Default New User." );
                Debug.WriteLine( ex.Message );
                Debug.WriteLine( "**********************************" );

                throw;
            }

            #endregion

            string demoPassword = "Xyz%987$";

            // These are my seeded demo users for showing off the software
            // Each user occupies a "main" role and the new Demo role
            // We will target this Demo role to prevent demo users from changing the database
            #region Demo Admin Seed

            defaultUser = new HeimdallUser
                          {
                              UserName       = "demomail01@mailinator.com",
                              Email          = "demomail01@mailinator.com",
                              FirstName      = "Fred",
                              LastName       = "Smith",
                              EmailConfirmed = true
                          };

            try
            {
                HeimdallUser user = await userManager.FindByEmailAsync( defaultUser.Email ).ConfigureAwait( false );

                if ( user == null )
                {
                    await userManager.CreateAsync( defaultUser, demoPassword ).ConfigureAwait( false );
                    await userManager.AddToRoleAsync( defaultUser, nameof( Roles.Administrator ) )
                                     .ConfigureAwait( false );
                    await userManager.AddToRoleAsync( defaultUser, nameof( Roles.DemoUser ) ).ConfigureAwait( false );
                }
            }
            catch ( Exception ex )
            {
                Debug.WriteLine( "************** ERROR *************" );
                Debug.WriteLine( "Error Seeding Demo Admin User." );
                Debug.WriteLine( ex.Message );
                Debug.WriteLine( "**********************************" );

                throw;
            }

            #endregion

            #region Demo Project Manager Seed

            defaultUser = new HeimdallUser
                          {
                              UserName       = "demomail02@mailinator.com",
                              Email          = "demomail02@mailinator.com",
                              FirstName      = "Bill",
                              LastName       = "Williams",
                              EmailConfirmed = true
                          };

            try
            {
                HeimdallUser user = await userManager.FindByEmailAsync( defaultUser.Email ).ConfigureAwait( false );

                if ( user == null )
                {
                    await userManager.CreateAsync( defaultUser, demoPassword ).ConfigureAwait( false );
                    await userManager.AddToRoleAsync( defaultUser, nameof( Roles.ProjectManager ) )
                                     .ConfigureAwait( false );
                    await userManager.AddToRoleAsync( defaultUser, nameof( Roles.DemoUser ) ).ConfigureAwait( false );
                }
            }
            catch ( Exception ex )
            {
                Debug.WriteLine( "************** ERROR *************" );
                Debug.WriteLine( "Error Seeding Demo Project Manager User." );
                Debug.WriteLine( ex.Message );
                Debug.WriteLine( "**********************************" );

                throw;
            }

            #endregion

            #region Demo Developer Seed

            defaultUser = new HeimdallUser
                          {
                              UserName       = "demomail03@mailinator.com",
                              Email          = "demomail03@mailinator.com",
                              FirstName      = "Nugz",
                              LastName       = "McNugz",
                              EmailConfirmed = true
                          };

            try
            {
                HeimdallUser user = await userManager.FindByEmailAsync( defaultUser.Email ).ConfigureAwait( false );

                if ( user == null )
                {
                    await userManager.CreateAsync( defaultUser, demoPassword ).ConfigureAwait( false );
                    await userManager.AddToRoleAsync( defaultUser, nameof( Roles.Developer ) ).ConfigureAwait( false );
                    await userManager.AddToRoleAsync( defaultUser, nameof( Roles.DemoUser ) ).ConfigureAwait( false );
                }
            }
            catch ( Exception ex )
            {
                Debug.WriteLine( "************** ERROR *************" );
                Debug.WriteLine( "Error Seeding Demo Developer User." );
                Debug.WriteLine( ex.Message );
                Debug.WriteLine( "**********************************" );

                throw;
            }

            #endregion

            #region Demo Submitter Seed

            defaultUser = new HeimdallUser
                          {
                              UserName       = "demomail04@mailinator.com",
                              Email          = "demomail04@mailinator.com",
                              FirstName      = "Nil",
                              LastName       = "Nullable",
                              EmailConfirmed = true
                          };

            try
            {
                HeimdallUser user = await userManager.FindByEmailAsync( defaultUser.Email ).ConfigureAwait( false );

                if ( user == null )
                {
                    await userManager.CreateAsync( defaultUser, demoPassword ).ConfigureAwait( false );
                    await userManager.AddToRoleAsync( defaultUser, nameof( Roles.Submitter ) ).ConfigureAwait( false );
                    await userManager.AddToRoleAsync( defaultUser, nameof( Roles.DemoUser ) ).ConfigureAwait( false );
                }
            }
            catch ( Exception ex )
            {
                Debug.WriteLine( "************** ERROR *************" );
                Debug.WriteLine( "Error Seeding Demo Submitter User." );
                Debug.WriteLine( ex.Message );
                Debug.WriteLine( "**********************************" );

                throw;
            }

            #endregion

            #region Demo New User Seed

            defaultUser = new HeimdallUser
                          {
                              UserName       = "demomail05@mailinator.com",
                              Email          = "demomail05@mailinator.com",
                              FirstName      = "Noob",
                              LastName       = "Neophyte",
                              EmailConfirmed = true
                          };

            try
            {
                HeimdallUser user = await userManager.FindByEmailAsync( defaultUser.Email ).ConfigureAwait( false );

                if ( user == null )
                {
                    await userManager.CreateAsync( defaultUser, demoPassword ).ConfigureAwait( false );
                    await userManager.AddToRoleAsync( defaultUser, nameof( Roles.NewUser ) ).ConfigureAwait( false );
                    await userManager.AddToRoleAsync( defaultUser, nameof( Roles.DemoUser ) ).ConfigureAwait( false );
                }
            }
            catch ( Exception ex )
            {
                Debug.WriteLine( "************** ERROR *************" );
                Debug.WriteLine( "Error Seeding Demo New User." );
                Debug.WriteLine( ex.Message );
                Debug.WriteLine( "**********************************" );

                throw;
            }

            #endregion
        }

        private static async Task SeedTicketTypesAsync( ApplicationDbContext context )
        {
            // Seed UI TicketType
            TicketType defaultSeedUi = new TicketType { Name = "UI" };

            try
            {
                TicketType type = await context.TicketTypes.Where( tt => tt.Name == "UI" )
                                               .FirstOrDefaultAsync( )
                                               .ConfigureAwait( false );

                if ( type == null )
                {
                    await context.TicketTypes.AddAsync( defaultSeedUi ).ConfigureAwait( false );
                }

                await context.SaveChangesAsync( ).ConfigureAwait( false );
            }
            catch ( Exception ex )
            {
                Debug.WriteLine( "************* ERROR *************" );
                Debug.WriteLine( "Error Seeding Default UI Ticket Type." );
                Debug.WriteLine( ex.Message );
                Debug.WriteLine( "*********************************" );
            }

            ;
        }

        private static async Task SeedTicketStatusesAsync( ApplicationDbContext context )
        {
            // Seed UI TicketType
            TicketStatus defaultSeedUi = new TicketStatus { Name = "UI" };

            try
            {
                TicketStatus type = await context.TicketStatuses.Where( tt => tt.Name == "UI" )
                                                 .FirstOrDefaultAsync( )
                                                 .ConfigureAwait( false );

                if ( type == null )
                {
                    await context.TicketStatuses.AddAsync( defaultSeedUi ).ConfigureAwait( false );
                }

                await context.SaveChangesAsync( ).ConfigureAwait( false );
            }
            catch ( Exception ex )
            {
                Debug.WriteLine( "************* ERROR *************" );
                Debug.WriteLine( "Error Seeding Default UI Ticket Type." );
                Debug.WriteLine( ex.Message );
                Debug.WriteLine( "*********************************" );
            }

            ;
        }

        private static async Task SeedTicketPrioritiesAsync( ApplicationDbContext context )
        {
            // Seed UI TicketType
            TicketPriority defaultSeedUi = new TicketPriority { Name = "UI" };

            try
            {
                TicketPriority type = await context.TicketPriorities.Where( tt => tt.Name == "UI" )
                                                   .FirstOrDefaultAsync( )
                                                   .ConfigureAwait( false );

                if ( type == null )
                {
                    await context.TicketPriorities.AddAsync( defaultSeedUi ).ConfigureAwait( false );
                }

                await context.SaveChangesAsync( ).ConfigureAwait( false );
            }
            catch ( Exception ex )
            {
                Debug.WriteLine( "************* ERROR *************" );
                Debug.WriteLine( "Error Seeding Default UI Ticket Type." );
                Debug.WriteLine( ex.Message );
                Debug.WriteLine( "*********************************" );
            }

            ;
        }

        private static async Task SeedProjectsAsync( ApplicationDbContext context )
        {
            Project seedProject1 = new Project { Name = "Blog Project" };

            try
            {
                Project project = await context.Projects.FirstOrDefaultAsync( p => p.Name == "Blog Project" )
                                               .ConfigureAwait( false );

                if ( project == null )
                {
                    await context.Projects.AddAsync( seedProject1 ).ConfigureAwait( false );
                    await context.SaveChangesAsync( ).ConfigureAwait( false );
                }
            }
            catch ( Exception ex )
            {
                Debug.WriteLine( "************* ERROR *************" );
                Debug.WriteLine( "Error Seeding Default Project 1." );
                Debug.WriteLine( ex.Message );
                Debug.WriteLine( "*********************************" );
            }

            ;

            Project seedProject2 = new Project { Name = "Bug Tracker Project" };

            try
            {
                Project project = await context.Projects.FirstOrDefaultAsync( p => p.Name == "Bug Tracker Project" )
                                               .ConfigureAwait( false );

                if ( project == null )
                {
                    await context.Projects.AddAsync( seedProject2 ).ConfigureAwait( false );
                    await context.SaveChangesAsync( ).ConfigureAwait( false );
                }
            }
            catch ( Exception ex )
            {
                Debug.WriteLine( "************* ERROR *************" );
                Debug.WriteLine( "Error Seeding Default Project 2." );
                Debug.WriteLine( ex.Message );
                Debug.WriteLine( "*********************************" );
            }

            ;

            Project seedProject3 = new Project { Name = "Financial Portal Project" };

            try
            {
                Project project = await context.Projects
                                               .FirstOrDefaultAsync( p => p.Name == "Financial Portal Project" )
                                               .ConfigureAwait( false );

                if ( project == null )
                {
                    await context.Projects.AddAsync( seedProject3 ).ConfigureAwait( false );
                    await context.SaveChangesAsync( ).ConfigureAwait( false );
                }
            }
            catch ( Exception ex )
            {
                Debug.WriteLine( "************* ERROR *************" );
                Debug.WriteLine( "Error Seeding Default Project 3." );
                Debug.WriteLine( ex.Message );
                Debug.WriteLine( "*********************************" );
            }

            ;
        }

        private static async Task SeedProjectUsersAsync(
            ApplicationDbContext      context,
            UserManager<HeimdallUser> userManager )
        {
            string administratorId =
                ( await userManager.FindByEmailAsync( "demomail01@mailinator.com" ).ConfigureAwait( false ) ).Id;
            string projectManagerId =
                ( await userManager.FindByEmailAsync( "demomail02@mailinator.com" ).ConfigureAwait( false ) ).Id;
            string developerId =
                ( await userManager.FindByEmailAsync( "demomail03@mailinator.com" ).ConfigureAwait( false ) ).Id;
            string submitterId =
                ( await userManager.FindByEmailAsync( "demomail04@mailinator.com" ).ConfigureAwait( false ) ).Id;
            int project1Id = ( await context.Projects.FirstOrDefaultAsync( p => p.Name == "Blog Project" )
                                            .ConfigureAwait( false ) ).Id;
            int project2Id = ( await context.Projects.FirstOrDefaultAsync( p => p.Name == "Bug Tracker Project" )
                                            .ConfigureAwait( false ) ).Id;
            int project3Id = ( await context.Projects.FirstOrDefaultAsync( p => p.Name == "Financial Portal Project" )
                                            .ConfigureAwait( false ) ).Id;
            ProjectUser projectUser = new ProjectUser { UserId = administratorId, ProjectId = project1Id };

            try
            {
                ProjectUser record = await context.ProjectUsers
                                                  .FirstOrDefaultAsync(
                                                                       r => r.UserId    == administratorId
                                                                         && r.ProjectId == project1Id )
                                                  .ConfigureAwait( false );

                if ( record == null )
                {
                    await context.ProjectUsers.AddAsync( projectUser ).ConfigureAwait( false );
                    await context.SaveChangesAsync( ).ConfigureAwait( false );
                }
            }
            catch ( Exception ex )
            {
                Debug.WriteLine( "************* ERROR *************" );
                Debug.WriteLine( "Error Seeding Admin Project 1." );
                Debug.WriteLine( ex.Message );
                Debug.WriteLine( "*********************************" );
            }

            ;
            projectUser = new ProjectUser { UserId = administratorId, ProjectId = project2Id };

            try
            {
                ProjectUser record = await context.ProjectUsers
                                                  .FirstOrDefaultAsync(
                                                                       r => r.UserId    == administratorId
                                                                         && r.ProjectId == project2Id )
                                                  .ConfigureAwait( false );

                if ( record == null )
                {
                    await context.ProjectUsers.AddAsync( projectUser ).ConfigureAwait( false );
                    await context.SaveChangesAsync( ).ConfigureAwait( false );
                }
            }
            catch ( Exception ex )
            {
                Debug.WriteLine( "************* ERROR *************" );
                Debug.WriteLine( "Error Seeding Admin Project 2." );
                Debug.WriteLine( ex.Message );
                Debug.WriteLine( "*********************************" );
            }

            ;
            projectUser = new ProjectUser { UserId = administratorId, ProjectId = project3Id };

            try
            {
                ProjectUser record = await context.ProjectUsers
                                                  .FirstOrDefaultAsync(
                                                                       r => r.UserId    == administratorId
                                                                         && r.ProjectId == project3Id )
                                                  .ConfigureAwait( false );

                if ( record == null )
                {
                    await context.ProjectUsers.AddAsync( projectUser ).ConfigureAwait( false );
                    await context.SaveChangesAsync( ).ConfigureAwait( false );
                }
            }
            catch ( Exception ex )
            {
                Debug.WriteLine( "************* ERROR *************" );
                Debug.WriteLine( "Error Seeding Admin Project 3." );
                Debug.WriteLine( ex.Message );
                Debug.WriteLine( "*********************************" );
            }

            ;
            projectUser = new ProjectUser { UserId = projectManagerId, ProjectId = project1Id };

            try
            {
                ProjectUser record = await context.ProjectUsers
                                                  .FirstOrDefaultAsync(
                                                                       r => r.UserId    == projectManagerId
                                                                         && r.ProjectId == project1Id )
                                                  .ConfigureAwait( false );

                if ( record == null )
                {
                    await context.ProjectUsers.AddAsync( projectUser ).ConfigureAwait( false );
                    await context.SaveChangesAsync( ).ConfigureAwait( false );
                }
            }
            catch ( Exception ex )
            {
                Debug.WriteLine( "************* ERROR *************" );
                Debug.WriteLine( "Error Seeding PM Project 1." );
                Debug.WriteLine( ex.Message );
                Debug.WriteLine( "*********************************" );
            }

            ;
            projectUser = new ProjectUser { UserId = projectManagerId, ProjectId = project2Id };

            try
            {
                ProjectUser record = await context.ProjectUsers
                                                  .FirstOrDefaultAsync(
                                                                       r => r.UserId    == projectManagerId
                                                                         && r.ProjectId == project2Id )
                                                  .ConfigureAwait( false );

                if ( record == null )
                {
                    await context.ProjectUsers.AddAsync( projectUser ).ConfigureAwait( false );
                    await context.SaveChangesAsync( ).ConfigureAwait( false );
                }
            }
            catch ( Exception ex )
            {
                Debug.WriteLine( "************* ERROR *************" );
                Debug.WriteLine( "Error Seeding PM Project 2." );
                Debug.WriteLine( ex.Message );
                Debug.WriteLine( "*********************************" );
            }

            ;
            projectUser = new ProjectUser { UserId = projectManagerId, ProjectId = project3Id };

            try
            {
                ProjectUser record = await context.ProjectUsers
                                                  .FirstOrDefaultAsync(
                                                                       r => r.UserId    == projectManagerId
                                                                         && r.ProjectId == project3Id )
                                                  .ConfigureAwait( false );

                if ( record == null )
                {
                    await context.ProjectUsers.AddAsync( projectUser ).ConfigureAwait( false );
                    await context.SaveChangesAsync( ).ConfigureAwait( false );
                }
            }
            catch ( Exception ex )
            {
                Debug.WriteLine( "************* ERROR *************" );
                Debug.WriteLine( "Error Seeding PM Project 3." );
                Debug.WriteLine( ex.Message );
                Debug.WriteLine( "*********************************" );
            }

            ;
        }

        private static async Task SeedTicketsAsync(
            ApplicationDbContext      context,
            UserManager<HeimdallUser> userManager )
        {
            string developerId =
                ( await userManager.FindByEmailAsync( "marianatester@mailinator.com" ).ConfigureAwait( false ) ).Id;
            string submitterId =
                ( await userManager.FindByEmailAsync( "lucytester@mailinator.com" ).ConfigureAwait( false ) ).Id;
            int project1Id = ( await context.Projects.FirstOrDefaultAsync( p => p.Name == "Blog Project" )
                                            .ConfigureAwait( false ) ).Id;
            int project2Id = ( await context.Projects.FirstOrDefaultAsync( p => p.Name == "Bug Tracker Project" )
                                            .ConfigureAwait( false ) ).Id;
            int project3Id = ( await context.Projects.FirstOrDefaultAsync( p => p.Name == "Financial Portal Project" )
                                            .ConfigureAwait( false ) ).Id;
            int statusId = ( await context.TicketStatuses.FirstOrDefaultAsync( ts => ts.Name == "UI" )
                                          .ConfigureAwait( false ) ).Id;
            int typeId =
                ( await context.TicketTypes.FirstOrDefaultAsync( tt => tt.Name == "UI" ).ConfigureAwait( false ) ).Id;
            int priorityId = ( await context.TicketPriorities.FirstOrDefaultAsync( tp => tp.Name == "UI" )
                                            .ConfigureAwait( false ) ).Id;

            Ticket ticket = new Ticket
                            {
                                Title = "Need more blog posts",
                                Description =
                                    "It's not a real blog when you only have a single post. Our users have requested you present more content. Without the content the Google crawlers will never up our organic ranking",
                                Created          = DateTimeOffset.Now.AddDays( -7 ),
                                Updated          = DateTimeOffset.Now.AddHours( -30 ),
                                ProjectId        = project1Id,
                                TicketPriorityId = priorityId,
                                TicketTypeId     = typeId,
                                TicketStatusId   = statusId,
                                DeveloperUserId  = developerId,
                                OwnerUserId      = submitterId
                            };

            try
            {
                Ticket newTicket = await context.Tickets.FirstOrDefaultAsync( t => t.Title == "Need more blog posts" )
                                                .ConfigureAwait( false );

                if ( newTicket == null )
                {
                    await context.Tickets.AddAsync( newTicket ).ConfigureAwait( false );
                    await context.SaveChangesAsync( ).ConfigureAwait( false );
                }
            }
            catch ( Exception ex )
            {
                Debug.WriteLine( "************* ERROR *************" );
                Debug.WriteLine( "Error Seeding Ticket 1." );
                Debug.WriteLine( ex.Message );
                Debug.WriteLine( "*********************************" );
            }

            ;
        }
    }
}
