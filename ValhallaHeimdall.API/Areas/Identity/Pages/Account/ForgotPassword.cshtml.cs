using System.ComponentModel.DataAnnotations;
using System.Text;
using System.Text.Encodings.Web;
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
    public class ForgotPasswordModel : PageModel
    {
        private readonly UserManager<HeimdallUser> userManager;

        private readonly IEmailSender emailSender;

        public ForgotPasswordModel( UserManager<HeimdallUser> userManager, IEmailSender emailSender )
        {
            this.userManager = userManager;
            this.emailSender = emailSender;
        }

        [BindProperty]
        public InputModel Input { get; set; }

        public class InputModel
        {
            [Required]
            [EmailAddress]
            public string Email { get; set; }
        }

        public async Task<IActionResult> OnPostAsync( )
        {
            if ( this.ModelState.IsValid )
            {
                HeimdallUser user = await this.userManager.FindByEmailAsync( this.Input.Email ).ConfigureAwait( false );

                if ( user == null || !await this.userManager.IsEmailConfirmedAsync( user ).ConfigureAwait( false ) )
                {

                    // Don't reveal that the user does not exist or is not confirmed
                    return this.RedirectToPage( "./ForgotPasswordConfirmation" );
                }

                // For more information on how to enable account confirmation and password reset please
                // visit https://go.microsoft.com/fwlink/?LinkID=532713
                string code = await this.userManager.GeneratePasswordResetTokenAsync( user ).ConfigureAwait( false );
                code = WebEncoders.Base64UrlEncode( Encoding.UTF8.GetBytes( code ) );
                string callbackUrl = this.Url.Page(
                                                   "/Account/ResetPassword",
                                                   null,
                                                   new { area = "Identity", code },
                                                   this.Request.Scheme );

                await this.emailSender.SendEmailAsync(
                                                      this.Input.Email,
                                                      "Reset Password",
                                                      $"Please reset your password by <a href='{HtmlEncoder.Default.Encode( callbackUrl )}'>clicking here</a>." )
                          .ConfigureAwait( false );

                return this.RedirectToPage( "./ForgotPasswordConfirmation" );
            }

            return this.Page( );
        }
    }
}
