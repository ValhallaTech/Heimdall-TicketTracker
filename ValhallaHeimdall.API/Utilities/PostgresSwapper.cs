using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using ValhallaHeimdall.BLL.Models;
using ValhallaHeimdall.DAL.Data;

namespace ValhallaHeimdall.API.Utilities
{
    public class PostgresSwapper
    {
        public static string GetConnectionString( IConfiguration configuration )
        {
            // The default connection string will come from appSettings like usual
            string connectionString = configuration.GetConnectionString( "DefaultConnection" );

            // It will be automatically overwritten if we are running on Heroku
            string? databaseUrl = Environment.GetEnvironmentVariable( "DATABASE_URL" );

            return string.IsNullOrEmpty( databaseUrl ) ? connectionString : BuildConnectionString( databaseUrl );
        }

        public static string BuildConnectionString( string postgresDatabaseUrl )
        {
            // Provides an object representation of a uniform resource identifier (URI) and easy access
            // to the parts of the URI.
            Uri      databaseUri = new Uri( postgresDatabaseUrl );
            string[] userInfo    = databaseUri.UserInfo.Split( ':' );

            // Provides a simple way to create and manage the contents of connection strings
            // used by the NpgsqlConnection class
            NpgsqlConnectionStringBuilder builder = new NpgsqlConnectionStringBuilder
                                                    {
                                                        Host     = databaseUri.Host,
                                                        Port     = databaseUri.Port,
                                                        Username = userInfo[0],
                                                        Password = userInfo[1],
                                                        Database = databaseUri.LocalPath.TrimStart( '/' )
                                                    };

            return builder.ToString( );
        }

        public static async Task ManageDataAsync( IHost host )
        {
            try
            {
                // This technique is used to obtain references to services
                using IServiceScope svcScope    = host.Services.CreateScope( );
                IServiceProvider    svcProvider = svcScope.ServiceProvider;

                // Seed Data
                IServiceProvider          services    = svcScope.ServiceProvider;
                ApplicationDbContext      context     = services.GetRequiredService<ApplicationDbContext>( );
                UserManager<HeimdallUser> userManager = services.GetRequiredService<UserManager<HeimdallUser>>( );
                RoleManager<IdentityRole> roleManager = services.GetRequiredService<RoleManager<IdentityRole>>( );

                await ContextSeed.RunSeedMethodsAsync( context, roleManager, userManager ).ConfigureAwait( false );

                // The service will run your migrations
                ApplicationDbContext dbContextSvc = svcProvider.GetRequiredService<ApplicationDbContext>( );
                await dbContextSvc.Database.MigrateAsync( ).ConfigureAwait( false );
            }
            catch ( Exception ex )
            {
                Console.WriteLine( $"Exception while running Manage Data -> {ex}" );
            }
        }
    }
}
