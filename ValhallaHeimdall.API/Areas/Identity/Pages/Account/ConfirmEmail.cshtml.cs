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
    public class ConfirmEmailModel : PageModel
    {
        private readonly UserManager<HeimdallUser> userManager;

        public ConfirmEmailModel( UserManager<HeimdallUser> userManager ) => this.userManager = userManager;

        [TempData]
        public string StatusMessage { get; set; }

        public async Task<IActionResult> OnGetAsync( string userId, string code )
        {
            if ( userId == null || code == null ) return this.RedirectToPage( "/Index" );

            HeimdallUser user = await this.userManager.FindByIdAsync( userId ).ConfigureAwait( false );

            if ( user == null ) return this.NotFound( $"Unable to load user with ID '{userId}'." );

            code = Encoding.UTF8.GetString( WebEncoders.Base64UrlDecode( code ) );
            IdentityResult result = await this.userManager.ConfirmEmailAsync( user, code ).ConfigureAwait( false );
            this.StatusMessage =
                result.Succeeded ? "Thank you for confirming your email." : "Error confirming your email.";

            return this.Page( );
        }
    }
}
