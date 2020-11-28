using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.UI.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.WebUtilities;
using ValhallaHeimdall.BLL.Models;

namespace ValhallaHeimdall.API.Areas.Identity.Pages.Account
{
    [AllowAnonymous]
    public class RegisterConfirmationModel : PageModel
    {
        private readonly UserManager<HeimdallUser> userManager;

        private readonly IEmailSender sender;

        public RegisterConfirmationModel( UserManager<HeimdallUser> userManager, IEmailSender sender )
        {
            this.userManager = userManager;
            this.sender      = sender;
        }

        public string Email { get; set; }

        public bool DisplayConfirmAccountLink { get; set; }

        public string EmailConfirmationUrl { get; set; }

        public async Task<IActionResult> OnGetAsync( string email, string returnUrl = null )
        {
            if ( email == null ) return this.RedirectToPage( "/Index" );

            HeimdallUser user = await this.userManager.FindByEmailAsync( email ).ConfigureAwait( false );

            if ( user == null ) return this.NotFound( $"Unable to load user with email '{email}'." );

            this.Email = email;

            // Once you add a real email sender, you should remove this code that lets you confirm the account
            this.DisplayConfirmAccountLink = true;

            if ( this.DisplayConfirmAccountLink )
            {
                string userId = await this.userManager.GetUserIdAsync( user ).ConfigureAwait( false );
                string code = await this.userManager.GenerateEmailConfirmationTokenAsync( user )
                                        .ConfigureAwait( false );
                code = WebEncoders.Base64UrlEncode( Encoding.UTF8.GetBytes( code ) );
                this.EmailConfirmationUrl = this.Url.Page(
                                                          "/Account/ConfirmEmail",
                                                          null,
                                                          new { area = "Identity", userId, code, returnUrl },
                                                          this.Request.Scheme );
            }

            return this.Page( );
        }
    }
}
