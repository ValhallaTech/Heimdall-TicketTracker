using System;
using System.ComponentModel.DataAnnotations;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Logging;
using ValhallaHeimdall.BLL.Models;
using SignInResult = Microsoft.AspNetCore.Identity.SignInResult;

namespace ValhallaHeimdall.API.Areas.Identity.Pages.Account
{
    [AllowAnonymous]
    public class LoginWith2FaModel : PageModel
    {
        private readonly SignInManager<HeimdallUser> signInManager;

        private readonly ILogger<LoginWith2FaModel> logger;

        public LoginWith2FaModel( SignInManager<HeimdallUser> signInManager, ILogger<LoginWith2FaModel> logger )
        {
            this.signInManager = signInManager;
            this.logger        = logger;
        }

        [BindProperty]
        public InputModel Input { get; set; }

        public bool RememberMe { get; set; }

        public string ReturnUrl { get; set; }

        public class InputModel
        {
            [Required]
            [StringLength(
                             7,
                             ErrorMessage  = "The {0} must be at least {2} and at max {1} characters long.",
                             MinimumLength = 6 )]
            [DataType( DataType.Text )]
            [Display( Name = "Authenticator code" )]
            public string TwoFactorCode { get; set; }

            [Display( Name = "Remember this machine" )]
            public bool RememberMachine { get; set; }
        }

        public async Task<IActionResult> OnGetAsync( bool rememberMe, string returnUrl = null )
        {
            // Ensure the user has gone through the username & password screen first
            HeimdallUser user = await this.signInManager.GetTwoFactorAuthenticationUserAsync( ).ConfigureAwait( false );

            if ( user == null ) throw new InvalidOperationException( "Unable to load two-factor authentication user." );

            this.ReturnUrl  = returnUrl;
            this.RememberMe = rememberMe;

            return this.Page( );
        }

        public async Task<IActionResult> OnPostAsync( bool rememberMe, string returnUrl = null )
        {
            if ( !this.ModelState.IsValid ) return this.Page( );

            returnUrl = returnUrl ?? this.Url.Content( "~/" );

            HeimdallUser user = await this.signInManager.GetTwoFactorAuthenticationUserAsync( ).ConfigureAwait( false );

            if ( user == null ) throw new InvalidOperationException( "Unable to load two-factor authentication user." );

            string authenticatorCode =
                this.Input.TwoFactorCode.Replace( " ", string.Empty ).Replace( "-", string.Empty );

            SignInResult result = await this.signInManager
                                            .TwoFactorAuthenticatorSignInAsync(
                                                                               authenticatorCode,
                                                                               rememberMe,
                                                                               this.Input.RememberMachine )
                                            .ConfigureAwait( false );

            if ( result.Succeeded )
            {
                this.logger.LogInformation( "User with ID '{UserId}' logged in with 2fa.", user.Id );

                return this.LocalRedirect( returnUrl );
            }

            if ( result.IsLockedOut )
            {
                this.logger.LogWarning( "User with ID '{UserId}' account locked out.", user.Id );

                return this.RedirectToPage( "./Lockout" );
            }

            this.logger.LogWarning( "Invalid authenticator code entered for user with ID '{UserId}'.", user.Id );
            this.ModelState.AddModelError( string.Empty, "Invalid authenticator code." );

            return this.Page( );
        }
    }
}
