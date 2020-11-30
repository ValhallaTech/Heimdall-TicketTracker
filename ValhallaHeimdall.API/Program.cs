using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ValhallaHeimdall.API.Utilities;
using ValhallaHeimdall.BLL.Models;
using ValhallaHeimdall.DAL.Data;

namespace ValhallaHeimdall.API
{
    public class Program
    {
        public static async Task Main( string[] args )
        {
            IHost host = CreateHostBuilder( args ).Build( );

            //await PostgresSwapper
            //      .ManageDataAsync( host )
            //      .ConfigureAwait( false );

            //await host
            //      .RunAsync( )
            //      .ConfigureAwait( false );

            using ( IServiceScope scope = host.Services.CreateScope( ) )
            {
                IServiceProvider services      = scope.ServiceProvider;
                ILoggerFactory   loggerFactory = services.GetRequiredService<ILoggerFactory>( );

                try
                {
                    ApplicationDbContext      context     = services.GetRequiredService<ApplicationDbContext>( );
                    UserManager<HeimdallUser> userManager = services.GetRequiredService<UserManager<HeimdallUser>>( );
                    RoleManager<IdentityRole> roleManager = services.GetRequiredService<RoleManager<IdentityRole>>( );
                    await ContextSeed.RunSeedMethodsAsync( context, roleManager, userManager ).ConfigureAwait( false );
                }
                catch ( Exception ex )
                {
                    ILogger<Program> logger = loggerFactory.CreateLogger<Program>( );
                    logger.LogError( ex, "An error occurred seeding the DB." );
                }
            }

            await host.RunAsync( ).ConfigureAwait( false );
        }

        public static IHostBuilder CreateHostBuilder( string[] args ) =>
            Host.CreateDefaultBuilder( args )
                .ConfigureWebHostDefaults( webBuilder => webBuilder.UseStartup<Startup>( ) );
    }
}
