using Microsoft.AspNetCore.Hosting;
using ValhallaHeimdall.API.Areas.Identity;
using ValhallaHeimdall.API.Areas.Identity.Pages.Account;

[assembly: HostingStartup( typeof( IdentityHostingStartup ) )]

namespace ValhallaHeimdall.API.Areas.Identity
{
    public class IdentityHostingStartup : IHostingStartup
    {
        public void Configure( IWebHostBuilder builder )
        {
            builder.ConfigureServices( ( context, services ) => { } );
        }
    }
}
