using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Logging;
using ValhallaHeimdall.BLL.Models;

namespace ValhallaHeimdall.API.Areas.Identity.Pages.Account.Manage
{
    public class Disable2FaModel : PageModel
    {
        private readonly UserManager<HeimdallUser> userManager;

        private readonly ILogger<Disable2FaModel> logger;

        public Disable2FaModel( UserManager<HeimdallUser> userManager, ILogger<Disable2FaModel> logger )
        {
            this.userManager = userManager;
            this.logger      = logger;
        }

        [TempData]
        public string StatusMessage { get; set; }

        public async Task<IActionResult> OnGet( )
        {
            HeimdallUser user = await this.userManager.GetUserAsync( this.User ).ConfigureAwait( false );

            if ( user == null )
                return this.NotFound( $"Unable to load user with ID '{this.userManager.GetUserId( this.User )}'." );

            if ( !await this.userManager.GetTwoFactorEnabledAsync( user ).ConfigureAwait( false ) )
            {
                throw new InvalidOperationException(
                                                    $"Cannot disable 2FA for user with ID '{this.userManager.GetUserId( this.User )}' as it's not currently enabled." );
            }

            return this.Page( );
        }

        public async Task<IActionResult> OnPostAsync( )
        {
            HeimdallUser user = await this.userManager.GetUserAsync( this.User ).ConfigureAwait( false );

            if ( user == null )
                return this.NotFound( $"Unable to load user with ID '{this.userManager.GetUserId( this.User )}'." );

            IdentityResult disable2FaResult =
                await this.userManager.SetTwoFactorEnabledAsync( user, false ).ConfigureAwait( false );

            if ( !disable2FaResult.Succeeded )
            {
                throw new InvalidOperationException(
                                                    $"Unexpected error occurred disabling 2FA for user with ID '{this.userManager.GetUserId( this.User )}'." );
            }

            this.logger.LogInformation(
                                       "User with ID '{UserId}' has disabled 2fa.",
                                       this.userManager.GetUserId( this.User ) );
            this.StatusMessage = "2fa has been disabled. You can reenable 2fa when you setup an authenticator app";

            return this.RedirectToPage( "./TwoFactorAuthentication" );
        }
    }
}
