using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.WebUtilities;
using ValhallaHeimdall.BLL.Models;

namespace ValhallaHeimdall.API.Areas.Identity.Pages.Account
{
    [AllowAnonymous]
    public class ConfirmEmailChangeModel : PageModel
    {
        private readonly UserManager<HeimdallUser> userManager;

        private readonly SignInManager<HeimdallUser> signInManager;

        public ConfirmEmailChangeModel(
            UserManager<HeimdallUser>   userManager,
            SignInManager<HeimdallUser> signInManager )
        {
            this.userManager   = userManager;
            this.signInManager = signInManager;
        }

        [TempData]
        public string StatusMessage { get; set; }

        public async Task<IActionResult> OnGetAsync( string userId, string email, string code )
        {
            if ( userId == null || email == null || code == null ) return this.RedirectToPage( "/Index" );

            HeimdallUser user = await this.userManager.FindByIdAsync( userId ).ConfigureAwait( false );

            if ( user == null ) return this.NotFound( $"Unable to load user with ID '{userId}'." );

            code = Encoding.UTF8.GetString( WebEncoders.Base64UrlDecode( code ) );
            IdentityResult result =
                await this.userManager.ChangeEmailAsync( user, email, code ).ConfigureAwait( false );

            if ( !result.Succeeded )
            {
                this.StatusMessage = "Error changing email.";

                return this.Page( );
            }

            // In our UI email and user name are one and the same, so when we update the email
            // we need to update the user name.
            IdentityResult setUserNameResult =
                await this.userManager.SetUserNameAsync( user, email ).ConfigureAwait( false );

            if ( !setUserNameResult.Succeeded )
            {
                this.StatusMessage = "Error changing user name.";

                return this.Page( );
            }

            await this.signInManager.RefreshSignInAsync( user ).ConfigureAwait( false );
            this.StatusMessage = "Thank you for confirming your email change.";

            return this.Page( );
        }
    }
}
