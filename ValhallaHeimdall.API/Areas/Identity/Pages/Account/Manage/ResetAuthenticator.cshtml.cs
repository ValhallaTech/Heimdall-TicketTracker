using System.Threading.Tasks;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Logging;
using ValhallaHeimdall.BLL.Models;

namespace ValhallaHeimdall.API.Areas.Identity.Pages.Account.Manage
{
    public class ResetAuthenticatorModel : PageModel
    {
        private readonly UserManager<HeimdallUser> userManager;

        private readonly SignInManager<HeimdallUser> signInManager;

        private readonly ILogger<ResetAuthenticatorModel> logger;

        public ResetAuthenticatorModel(
            UserManager<HeimdallUser>        userManager,
            SignInManager<HeimdallUser>      signInManager,
            ILogger<ResetAuthenticatorModel> logger )
        {
            this.userManager   = userManager;
            this.signInManager = signInManager;
            this.logger        = logger;
        }

        [TempData]
        public string StatusMessage { get; set; }

        public async Task<IActionResult> OnGet( )
        {
            HeimdallUser user = await this.userManager.GetUserAsync( this.User ).ConfigureAwait( false );

            if ( user == null )
                return this.NotFound( $"Unable to load user with ID '{this.userManager.GetUserId( this.User )}'." );

            return this.Page( );
        }

        public async Task<IActionResult> OnPostAsync( )
        {
            HeimdallUser user = await this.userManager.GetUserAsync( this.User ).ConfigureAwait( false );

            if ( user == null )
                return this.NotFound( $"Unable to load user with ID '{this.userManager.GetUserId( this.User )}'." );

            await this.userManager.SetTwoFactorEnabledAsync( user, false ).ConfigureAwait( false );
            await this.userManager.ResetAuthenticatorKeyAsync( user ).ConfigureAwait( false );
            this.logger.LogInformation( "User with ID '{UserId}' has reset their authentication app key.", user.Id );

            await this.signInManager.RefreshSignInAsync( user ).ConfigureAwait( false );
            this.StatusMessage =
                "Your authenticator app key has been reset, you will need to configure your authenticator app using the new key.";

            return this.RedirectToPage( "./EnableAuthenticator" );
        }
    }
}
