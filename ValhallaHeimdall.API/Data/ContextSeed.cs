using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Identity;
using ValhallaHeimdall.Models;

namespace ValhallaHeimdall.Data
{
    using System.Linq;

    public enum Roles
    {
        Administrator,

        ProjectManager,

        Developer,

        Submitter,

        NewUser
    }

    public enum TicketTypes
    {
        UI,

        Calculation,

        Logic,

        Security
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

        Testing,

        Development,

        QA,

        FinalPass,

        Closed
    }

    public static class ContextSeed
    {
        public static async Task SeedRolesAsync( RoleManager<IdentityRole> roleManager )
        {
            await roleManager.CreateAsync( new IdentityRole( nameof( Roles.Administrator ) ) ).ConfigureAwait( false );
            await roleManager.CreateAsync( new IdentityRole( nameof( Roles.ProjectManager ) ) ).ConfigureAwait( false );
            await roleManager.CreateAsync( new IdentityRole( nameof( Roles.Developer ) ) ).ConfigureAwait( false );
            await roleManager.CreateAsync( new IdentityRole( nameof( Roles.Submitter ) ) ).ConfigureAwait( false );
            await roleManager.CreateAsync( new IdentityRole( nameof( Roles.NewUser ) ) ).ConfigureAwait( false );
        }

        public static async Task SeedDefaultTicketPrioritiesAsync( ApplicationDbContext context )
        {
            try
            {
                if ( !context.TicketPriorities.Any( tp => tp.Name == "Low" ) )
                {
                    await context.TicketPriorities.AddAsync( new TicketPriority { Name = "Low" } )
                                 .ConfigureAwait( false );
                }

                if ( !context.TicketPriorities.Any( tp => tp.Name == "High" ) )
                {
                    await context.TicketPriorities.AddAsync( new TicketPriority { Name = "High" } )
                                 .ConfigureAwait( false );
                }

                if ( !context.TicketPriorities.Any( tp => tp.Name == "Blocker" ) )
                {
                    await context.TicketPriorities.AddAsync( new TicketPriority { Name = "Blocker" } )
                                 .ConfigureAwait( false );
                }

                if ( !context.TicketPriorities.Any( tp => tp.Name == "Pending" ) )
                {
                    await context.TicketPriorities.AddAsync( new TicketPriority { Name = "Pending" } )
                                 .ConfigureAwait( false );
                }

                await context.SaveChangesAsync( ).ConfigureAwait( false );
            }
            catch ( Exception ex )
            {
                Debug.WriteLine( "**************** ERROR ****************" );
                Debug.WriteLine( "Error Seeding Ticket Priorities" );
                Debug.WriteLine( ex.Message );
                Debug.WriteLine( "************************************************" );

                throw;
            }
        }

        public static async Task SeedDefaultTicketStatusesAsync( ApplicationDbContext context )
        {
            try
            {
                if ( !context.TicketStatuses.Any( ts => ts.Name == "Pending" ) )
                {
                    await context.TicketStatuses.AddAsync( new TicketStatus { Name = "Pending" } )
                                 .ConfigureAwait( false );
                }

                if ( !context.TicketStatuses.Any( ts => ts.Name == "In-Progress" ) )
                {
                    await context.TicketStatuses.AddAsync( new TicketStatus { Name = "In-Progress" } )
                                 .ConfigureAwait( false );
                }

                if ( !context.TicketStatuses.Any( ts => ts.Name == "Completed" ) )
                {
                    await context.TicketStatuses.AddAsync( new TicketStatus { Name = "Completed" } )
                                 .ConfigureAwait( false );
                }

                await context.SaveChangesAsync( ).ConfigureAwait( false );
            }
            catch ( Exception ex )
            {
                Debug.WriteLine( "**************** ERROR ****************" );
                Debug.WriteLine( "Error Seeding Ticket Statuses" );
                Debug.WriteLine( ex.Message );
                Debug.WriteLine( "************************************************" );

                throw;
            }
        }

        public static async Task SeedDefaultTicketTypesAsync( ApplicationDbContext context )
        {
            try
            {
                if ( !context.TicketTypes.Any( tt => tt.Name == "Front-End" ) )
                {
                    await context.TicketTypes.AddAsync( new TicketType { Name = "Front-End" } ).ConfigureAwait( false );
                }

                if ( !context.TicketTypes.Any( tt => tt.Name == "Back-End" ) )
                {
                    await context.TicketTypes.AddAsync( new TicketType { Name = "Back-End" } ).ConfigureAwait( false );
                }

                if ( !context.TicketTypes.Any( tt => tt.Name == "Miscellaneous" ) )
                {
                    await context.TicketTypes.AddAsync( new TicketType { Name = "Miscellaneous" } )
                                 .ConfigureAwait( false );
                }

                await context.SaveChangesAsync( ).ConfigureAwait( false );
            }
            catch ( Exception ex )
            {
                Debug.WriteLine( "**************** ERROR ****************" );
                Debug.WriteLine( "Error Seeding Ticket Types" );
                Debug.WriteLine( ex.Message );
                Debug.WriteLine( "************************************************" );

                throw;
            }
        }

        public static async Task SeedDefaultUsersAsync( UserManager<HeimdallUser> userManager )
        {
            var defaultAdministrator = new HeimdallUser
                                       {
                                           UserName       = "testmail01@mailinator.com",
                                           Email          = "testmail01@mailinator.com",
                                           FirstName      = "Fred",
                                           LastName       = "Smith",
                                           EmailConfirmed = true
                                       };

            try
            {
                var user = await userManager.FindByEmailAsync( defaultAdministrator.Email ).ConfigureAwait( false );
            }
            catch ( Exception ex )
            {
                Debug.WriteLine( "Error Seeding Default Admin User." );
                Debug.WriteLine( ex.Message );

                throw;
            }

            var defaultProjectManager = new HeimdallUser
                                        {
                                            UserName       = "testmail02@mailinator.com",
                                            Email          = "testmail02@mailinator.com",
                                            FirstName      = "Bill",
                                            LastName       = "Williams",
                                            EmailConfirmed = true
                                        };

            try
            {
                var user = await userManager.FindByEmailAsync( defaultProjectManager.Email ).ConfigureAwait( false );
            }
            catch ( Exception ex )
            {
                Debug.WriteLine( "Error Seeding Default Project Manager User." );
                Debug.WriteLine( ex.Message );

                throw;
            }

            var defaultDeveloper = new HeimdallUser
                                   {
                                       UserName       = "testmail03@mailinator.com",
                                       Email          = "testmail03@mailinator.com",
                                       FirstName      = "Nugs",
                                       LastName       = "McNuggets",
                                       EmailConfirmed = true
                                   };

            try
            {
                var user = await userManager.FindByEmailAsync( defaultDeveloper.Email ).ConfigureAwait( false );
            }
            catch ( Exception ex )
            {
                Debug.WriteLine( "Error Seeding Default Developer User." );
                Debug.WriteLine( ex.Message );

                throw;
            }

            var defaultSubmitter = new HeimdallUser
                                   {
                                       UserName       = "testmail04@mailinator.com",
                                       Email          = "testmail04@mailinator.com",
                                       FirstName      = "Spicy",
                                       LastName       = "Bacon",
                                       EmailConfirmed = true
                                   };

            try
            {
                var user = await userManager.FindByEmailAsync( defaultSubmitter.Email ).ConfigureAwait( false );
            }
            catch ( Exception ex )
            {
                Debug.WriteLine( "Error Seeding Default Submitter User." );
                Debug.WriteLine( ex.Message );

                throw;
            }

            var defaultNewUser = new HeimdallUser
                                 {
                                     UserName       = "testmail05@mailinator.com",
                                     Email          = "testmail05@mailinator.com",
                                     FirstName      = "Nil",
                                     LastName       = "Nullable",
                                     EmailConfirmed = true
                                 };

            try
            {
                var user = await userManager.FindByEmailAsync( defaultNewUser.Email ).ConfigureAwait( false );
            }
            catch ( Exception ex )
            {
                Debug.WriteLine( "Error Seeding Default New User." );
                Debug.WriteLine( ex.Message );

                throw;
            }
        }
    }
}
