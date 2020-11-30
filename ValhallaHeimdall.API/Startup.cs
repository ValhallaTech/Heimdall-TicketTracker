using MailKit;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.UI.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ValhallaHeimdall.API.Services;
using ValhallaHeimdall.API.Utilities;
using ValhallaHeimdall.BLL.Models;
using ValhallaHeimdall.DAL.Data;

namespace ValhallaHeimdall.API
{
    public class Startup
    {
        public Startup( IConfiguration configuration ) => this.Configuration = configuration;

        public IConfiguration Configuration { get; }

        // This method gets called by the runtime. Use this method to add services to the container.
        public void ConfigureServices( IServiceCollection services )
        {
            // Adds the controllers to the DI container
            services.AddControllers( ).AddControllersAsServices( );

            services.AddDbContext<ApplicationDbContext>(
                                                        options => options.UseSqlServer(
                                                         this.Configuration.GetConnectionString(
                                                          "DefaultConnection" ) ) );

            //services.AddDbContext<ApplicationDbContext>(
            //                                            options => options.UseNpgsql(
            //                                             PostgresSwapper
            //                                                 .GetConnectionString( this.Configuration ) ) );

            services.AddIdentity<HeimdallUser, IdentityRole>( options => options.SignIn.RequireConfirmedAccount = true )
                    .AddEntityFrameworkStores<ApplicationDbContext>( )
                    .AddDefaultUI( )
                    .AddDefaultTokenProviders( );

            services.AddScoped<IHeimdallRolesService, HeimdallRolesService>( );
            services.AddScoped<IHeimdallProjectService, HeimdallProjectService>( );
            services.AddScoped<IHeimdallHistoryService, HeimdallHistoryService>( );
            services.AddScoped<IHeimdallAccessService, HeimdallAccessService>( );
            services.AddScoped<IHeimdallFileService, HeimdallFileService>( );
            services.AddScoped<IHeimdallNotificationService, HeimdallNotificationService>( );

            services.Configure<MailSettings>( this.Configuration.GetSection( "MailSettings" ) );
            services.AddTransient<IEmailSender, HeimdallEmailService>( );

            services.AddControllersWithViews( );
            services.AddRazorPages( );
        }

        // This method gets called by the runtime. Use this method to configure the HTTP request pipeline.
        public void Configure( IApplicationBuilder app, IWebHostEnvironment env )
        {
            if ( env.IsDevelopment( ) )
            {
                app.UseDeveloperExceptionPage( );
                app.UseDatabaseErrorPage( );
            }
            else
            {
                app.UseExceptionHandler( "/Home/Error" );

                // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
                app.UseHsts( );
            }

            app.UseHttpsRedirection( );
            app.UseStaticFiles( );
            app.UseCookiePolicy( );
            app.UseRouting( );
            app.UseAuthentication( );
            app.UseAuthorization( );
            app.UseSession( );
            app.UseResponseCaching( );

            app.UseEndpoints(
                             endpoints =>
                             {
                                 endpoints.MapControllerRoute(
                                                              name: "default",
                                                              pattern: "{controller=Home}/{action=Index}/{id?}" );
                                 endpoints.MapRazorPages( );
                             } );
        }
    }
}
