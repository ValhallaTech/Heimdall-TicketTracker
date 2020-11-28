using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.UI.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Logging;
using ValhallaHeimdall.BLL.Models;
using SignInResult = Microsoft.AspNetCore.Identity.SignInResult;

namespace ValhallaHeimdall.API.Areas.Identity.Pages.Account
{
    [AllowAnonymous]
    public class ExternalLoginModel : PageModel
    {
        private readonly SignInManager<HeimdallUser> signInManager;

        private readonly UserManager<HeimdallUser> userManager;

        private readonly IEmailSender emailSender;

        private readonly ILogger<ExternalLoginModel> logger;

        public ExternalLoginModel(
            SignInManager<HeimdallUser> signInManager,
            UserManager<HeimdallUser>   userManager,
            ILogger<ExternalLoginModel> logger,
            IEmailSender                emailSender )
        {
            this.signInManager = signInManager;
            this.userManager   = userManager;
            this.logger        = logger;
            this.emailSender   = emailSender;
        }

        [BindProperty]
        public InputModel Input { get; set; }

        public string ProviderDisplayName { get; set; }

        public string ReturnUrl { get; set; }

        [TempData]
        public string ErrorMessage { get; set; }

        public class InputModel
        {
            [Required]
            [EmailAddress]
            public string Email { get; set; }
        }

        public async Task<IActionResult> OnGetAsync( ) => this.RedirectToPage( "./Login" );

        public IActionResult OnPost( string provider, string returnUrl = null )
        {
            // Request a redirect to the external login provider.
            string redirectUrl = this.Url.Page( "./ExternalLogin", "Callback", new { returnUrl } );
            AuthenticationProperties properties =
                this.signInManager.ConfigureExternalAuthenticationProperties( provider, redirectUrl );

            return new ChallengeResult( provider, properties );
        }

        public async Task<IActionResult> OnGetCallbackAsync( string returnUrl = null, string remoteError = null )
        {
            returnUrl ??= this.Url.Content( "~/" );

            if ( remoteError != null )
            {
                this.ErrorMessage = $"Error from external provider: {remoteError}";

                return this.RedirectToPage( "./Login", new { ReturnUrl = returnUrl } );
            }

            ExternalLoginInfo info = await this.signInManager.GetExternalLoginInfoAsync( ).ConfigureAwait( false );

            if ( info == null )
            {
                this.ErrorMessage = "Error loading external login information.";

                return this.RedirectToPage( "./Login", new { ReturnUrl = returnUrl } );
            }

            // Sign in the user with this external login provider if the user already has a login.
            SignInResult result = await this.signInManager
                                            .ExternalLoginSignInAsync(
                                                                      info.LoginProvider,
                                                                      info.ProviderKey,
                                                                      false,
                                                                      true )
                                            .ConfigureAwait( false );

            if ( result.Succeeded )
            {
                this.logger.LogInformation(
                                           "{Name} logged in with {LoginProvider} provider.",
                                           info.Principal.Identity.Name,
                                           info.LoginProvider );

                return this.LocalRedirect( returnUrl );
            }

            if ( result.IsLockedOut )
            {
                return this.RedirectToPage( "./Lockout" );
            }

            // If the user does not have an account, then ask the user to create an account.
            this.ReturnUrl           = returnUrl;
            this.ProviderDisplayName = info.ProviderDisplayName;

            if ( info.Principal.HasClaim( c => c.Type == ClaimTypes.Email ) )
                this.Input = new InputModel { Email = info.Principal.FindFirstValue( ClaimTypes.Email ) };

            return this.Page( );
        }

        public async Task<IActionResult> OnPostConfirmationAsync( string returnUrl = null )
        {
            returnUrl ??= this.Url.Content( "~/" );

            // Get the information about the user from the external login provider
            ExternalLoginInfo info = await this.signInManager.GetExternalLoginInfoAsync( ).ConfigureAwait( false );

            if ( info == null )
            {
                this.ErrorMessage = "Error loading external login information during confirmation.";

                return this.RedirectToPage( "./Login", new { ReturnUrl = returnUrl } );
            }

            if ( this.ModelState.IsValid )
            {
                HeimdallUser user = new HeimdallUser { UserName = this.Input.Email, Email = this.Input.Email };

                IdentityResult result = await this.userManager.CreateAsync( user ).ConfigureAwait( false );

                if ( result.Succeeded )
                {
                    result = await this.userManager.AddLoginAsync( user, info ).ConfigureAwait( false );

                    if ( result.Succeeded )
                    {
                        this.logger.LogInformation(
                                                   "User created an account using {Name} provider.",
                                                   info.LoginProvider );

                        string userId = await this.userManager.GetUserIdAsync( user ).ConfigureAwait( false );
                        string code = await this.userManager.GenerateEmailConfirmationTokenAsync( user )
                                                .ConfigureAwait( false );
                        code = WebEncoders.Base64UrlEncode( Encoding.UTF8.GetBytes( code ) );
                        string callbackUrl = this.Url.Page(
                                                           "/Account/ConfirmEmail",
                                                           null,
                                                           new { area = "Identity", userId, code },
                                                           this.Request.Scheme );

                        await this.emailSender.SendEmailAsync(
                                                              this.Input.Email,
                                                              "Confirm your email",
                                                              $"Please confirm your account by <a href='{HtmlEncoder.Default.Encode( callbackUrl )}'>clicking here</a>." )
                                  .ConfigureAwait( false );

                        // If account confirmation is required, we need to show the link if we don't have a real email sender
                        if ( this.userManager.Options.SignIn.RequireConfirmedAccount )
                            return this.RedirectToPage( "./RegisterConfirmation", new { this.Input.Email } );

                        await this.signInManager.SignInAsync( user, false, info.LoginProvider ).ConfigureAwait( false );

                        return this.LocalRedirect( returnUrl );
                    }
                }

                foreach ( IdentityError error in result.Errors )
                    this.ModelState.AddModelError( string.Empty, error.Description );
            }

            this.ProviderDisplayName = info.ProviderDisplayName;
            this.ReturnUrl           = returnUrl;

            return this.Page( );
        }
    }
}
