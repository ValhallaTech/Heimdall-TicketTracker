using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Logging;
using ValhallaHeimdall.BLL.Models;

namespace ValhallaHeimdall.API.Areas.Identity.Pages.Account.Manage
{
    public class DownloadPersonalDataModel : PageModel
    {
        private readonly UserManager<HeimdallUser> userManager;

        private readonly ILogger<DownloadPersonalDataModel> logger;

        public DownloadPersonalDataModel(
            UserManager<HeimdallUser>          userManager,
            ILogger<DownloadPersonalDataModel> logger )
        {
            this.userManager = userManager;
            this.logger      = logger;
        }

        public async Task<IActionResult> OnPostAsync( )
        {
            HeimdallUser user = await this.userManager.GetUserAsync( this.User ).ConfigureAwait( false );

            if ( user == null )
                return this.NotFound( $"Unable to load user with ID '{this.userManager.GetUserId( this.User )}'." );

            this.logger.LogInformation(
                                       "User with ID '{UserId}' asked for their personal data.",
                                       this.userManager.GetUserId( this.User ) );

            // Only include personal data for download
            Dictionary<string, string> personalData = new Dictionary<string, string>( );
            IEnumerable<PropertyInfo> personalDataProps = typeof( HeimdallUser ).GetProperties( )
                                                                                .Where( prop => Attribute.IsDefined( prop, typeof( PersonalDataAttribute ) ) );

            foreach ( PropertyInfo p in personalDataProps )
                personalData.Add( p.Name, p.GetValue( user )?.ToString( ) ?? "null" );

            IList<UserLoginInfo> logins = await this.userManager.GetLoginsAsync( user ).ConfigureAwait( false );

            foreach ( UserLoginInfo l in logins )
                personalData.Add( $"{l.LoginProvider} external login provider key", l.ProviderKey );

            this.Response.Headers.Add( "Content-Disposition", "attachment; filename=PersonalData.json" );

            return new FileContentResult( JsonSerializer.SerializeToUtf8Bytes( personalData ), "application/json" );
        }
    }
}
