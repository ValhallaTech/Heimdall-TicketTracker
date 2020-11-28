using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using ValhallaHeimdall.BLL.Models;

namespace ValhallaHeimdall.API.Areas.Identity.Pages.Account.Manage
{
    public class ExternalLoginsModel : PageModel
    {
        private readonly UserManager<HeimdallUser> userManager;

        private readonly SignInManager<HeimdallUser> signInManager;

        public ExternalLoginsModel( UserManager<HeimdallUser> userManager, SignInManager<HeimdallUser> signInManager )
        {
            this.userManager   = userManager;
            this.signInManager = signInManager;
        }

        public IList<UserLoginInfo> CurrentLogins { get; set; }

        public IList<AuthenticationScheme> OtherLogins { get; set; }

        public bool ShowRemoveButton { get; set; }

        [TempData]
        public string StatusMessage { get; set; }

        public async Task<IActionResult> OnGetAsync( )
        {
            HeimdallUser user = await this.userManager.GetUserAsync( this.User ).ConfigureAwait( false );

            if ( user == null ) return this.NotFound( "Unable to load user with ID 'user.Id'." );

            this.CurrentLogins = await this.userManager.GetLoginsAsync( user ).ConfigureAwait( false );
            this.OtherLogins =
                ( await this.signInManager.GetExternalAuthenticationSchemesAsync( ).ConfigureAwait( false ) )
                .Where( auth => this.CurrentLogins.All( ul => auth.Name != ul.LoginProvider ) )
                .ToList( );
            this.ShowRemoveButton = user.PasswordHash != null || this.CurrentLogins.Count > 1;

            return this.Page( );
        }

        public async Task<IActionResult> OnPostRemoveLoginAsync( string loginProvider, string providerKey )
        {
            HeimdallUser user = await this.userManager.GetUserAsync( this.User ).ConfigureAwait( false );

            if ( user == null ) return this.NotFound( "Unable to load user with ID 'user.Id'." );

            IdentityResult result = await this.userManager.RemoveLoginAsync( user, loginProvider, providerKey )
                                              .ConfigureAwait( false );

            if ( !result.Succeeded )
            {
                this.StatusMessage = "The external login was not removed.";

                return this.RedirectToPage( );
            }

            await this.signInManager.RefreshSignInAsync( user ).ConfigureAwait( false );
            this.StatusMessage = "The external login was removed.";

            return this.RedirectToPage( );
        }

        public async Task<IActionResult> OnPostLinkLoginAsync( string provider )
        {
            // Clear the existing external cookie to ensure a clean login process
            await this.HttpContext.SignOutAsync( IdentityConstants.ExternalScheme ).ConfigureAwait( false );

            // Request a redirect to the external login provider to link a login for the current user
            string redirectUrl = this.Url.Page( "./ExternalLogins", "LinkLoginCallback" );
            AuthenticationProperties properties =
                this.signInManager.ConfigureExternalAuthenticationProperties(
                                                                             provider,
                                                                             redirectUrl,
                                                                             this.userManager.GetUserId( this.User ) );

            return new ChallengeResult( provider, properties );
        }

        public async Task<IActionResult> OnGetLinkLoginCallbackAsync( )
        {
            HeimdallUser user = await this.userManager.GetUserAsync( this.User ).ConfigureAwait( false );

            if ( user == null ) return this.NotFound( "Unable to load user with ID 'user.Id'." );

            ExternalLoginInfo info =
                await this.signInManager.GetExternalLoginInfoAsync( user.Id ).ConfigureAwait( false );

            if ( info == null )
            {
                throw new InvalidOperationException(
                                                    $"Unexpected error occurred loading external login info for user with ID '{user.Id}'." );
            }

            IdentityResult result = await this.userManager.AddLoginAsync( user, info ).ConfigureAwait( false );

            if ( !result.Succeeded )
            {
                this.StatusMessage =
                    "The external login was not added. External logins can only be associated with one account.";

                return this.RedirectToPage( );
            }

            // Clear the existing external cookie to ensure a clean login process
            await this.HttpContext.SignOutAsync( IdentityConstants.ExternalScheme ).ConfigureAwait( false );

            this.StatusMessage = "The external login was added.";

            return this.RedirectToPage( );
        }
    }
}
