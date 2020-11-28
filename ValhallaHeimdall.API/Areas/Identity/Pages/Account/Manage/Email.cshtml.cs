using System.ComponentModel.DataAnnotations;
using System.Text;
using System.Text.Encodings.Web;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.UI.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.WebUtilities;
using ValhallaHeimdall.BLL.Models;

namespace ValhallaHeimdall.API.Areas.Identity.Pages.Account.Manage
{
    public class EmailModel : PageModel
    {
        private readonly UserManager<HeimdallUser> userManager;

        private readonly SignInManager<HeimdallUser> signInManager;

        private readonly IEmailSender emailSender;

        public EmailModel(
            UserManager<HeimdallUser>   userManager,
            SignInManager<HeimdallUser> signInManager,
            IEmailSender                emailSender )
        {
            this.userManager   = userManager;
            this.signInManager = signInManager;
            this.emailSender   = emailSender;
        }

        public string Username { get; set; }

        public string Email { get; set; }

        public bool IsEmailConfirmed { get; set; }

        [TempData]
        public string StatusMessage { get; set; }

        [BindProperty]
        public InputModel Input { get; set; }

        public class InputModel
        {
            [Required]
            [EmailAddress]
            [Display( Name = "New email" )]
            public string NewEmail { get; set; }
        }

        private async Task LoadAsync( HeimdallUser user )
        {
            string email = await this.userManager.GetEmailAsync( user ).ConfigureAwait( false );
            this.Email = email;

            this.Input = new InputModel { NewEmail = email };

            this.IsEmailConfirmed = await this.userManager.IsEmailConfirmedAsync( user ).ConfigureAwait( false );
        }

        public async Task<IActionResult> OnGetAsync( )
        {
            HeimdallUser user = await this.userManager.GetUserAsync( this.User ).ConfigureAwait( false );

            if ( user == null )
                return this.NotFound( $"Unable to load user with ID '{this.userManager.GetUserId( this.User )}'." );

            await this.LoadAsync( user ).ConfigureAwait( false );

            return this.Page( );
        }

        public async Task<IActionResult> OnPostChangeEmailAsync( )
        {
            HeimdallUser user = await this.userManager.GetUserAsync( this.User ).ConfigureAwait( false );

            if ( user == null )
                return this.NotFound( $"Unable to load user with ID '{this.userManager.GetUserId( this.User )}'." );

            if ( !this.ModelState.IsValid )
            {
                await this.LoadAsync( user ).ConfigureAwait( false );

                return this.Page( );
            }

            string email = await this.userManager.GetEmailAsync( user ).ConfigureAwait( false );

            if ( this.Input.NewEmail != email )
            {
                string userId = await this.userManager.GetUserIdAsync( user ).ConfigureAwait( false );
                string code = await this.userManager.GenerateChangeEmailTokenAsync( user, this.Input.NewEmail )
                                        .ConfigureAwait( false );
                string callbackUrl = this.Url.Page(
                                                   "/Account/ConfirmEmailChange",
                                                   null,
                                                   new { userId, email = this.Input.NewEmail, code },
                                                   this.Request.Scheme );
                await this.emailSender.SendEmailAsync(
                                                      this.Input.NewEmail,
                                                      "Confirm your email",
                                                      $"Please confirm your account by <a href='{HtmlEncoder.Default.Encode( callbackUrl )}'>clicking here</a>." )
                          .ConfigureAwait( false );

                this.StatusMessage = "Confirmation link to change email sent. Please check your email.";

                return this.RedirectToPage( );
            }

            this.StatusMessage = "Your email is unchanged.";

            return this.RedirectToPage( );
        }

        public async Task<IActionResult> OnPostSendVerificationEmailAsync( )
        {
            HeimdallUser user = await this.userManager.GetUserAsync( this.User ).ConfigureAwait( false );

            if ( user == null )
                return this.NotFound( $"Unable to load user with ID '{this.userManager.GetUserId( this.User )}'." );

            if ( !this.ModelState.IsValid )
            {
                await this.LoadAsync( user ).ConfigureAwait( false );

                return this.Page( );
            }

            string userId = await this.userManager.GetUserIdAsync( user ).ConfigureAwait( false );
            string email  = await this.userManager.GetEmailAsync( user ).ConfigureAwait( false );
            string code   = await this.userManager.GenerateEmailConfirmationTokenAsync( user ).ConfigureAwait( false );
            code = WebEncoders.Base64UrlEncode( Encoding.UTF8.GetBytes( code ) );
            string callbackUrl = this.Url.Page(
                                               "/Account/ConfirmEmail",
                                               null,
                                               new { area = "Identity", userId, code },
                                               this.Request.Scheme );
            await this.emailSender.SendEmailAsync(
                                                  email,
                                                  "Confirm your email",
                                                  $"Please confirm your account by <a href='{HtmlEncoder.Default.Encode( callbackUrl )}'>clicking here</a>." )
                      .ConfigureAwait( false );

            this.StatusMessage = "Verification email sent. Please check your email.";

            return this.RedirectToPage( );
        }
    }
}
