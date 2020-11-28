using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authentication;
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
    public class LoginModel : PageModel
    {
        private readonly UserManager<HeimdallUser> userManager;

        private readonly SignInManager<HeimdallUser> signInManager;

        private readonly ILogger<LoginModel> logger;

        public LoginModel(
            SignInManager<HeimdallUser> signInManager,
            ILogger<LoginModel>         logger,
            UserManager<HeimdallUser>   userManager )
        {
            this.userManager   = userManager;
            this.signInManager = signInManager;
            this.logger        = logger;
        }

        [BindProperty]
        public InputModel Input { get; set; }

        public IList<AuthenticationScheme> ExternalLogins { get; set; }

        public string ReturnUrl { get; set; }

        [TempData]
        public string ErrorMessage { get; set; }

        public class InputModel
        {
            [Required]
            [EmailAddress]
            public string Email { get; set; }

            [Required]
            [DataType( DataType.Password )]
            public string Password { get; set; }

            [Display( Name = "Remember me?" )]
            public bool RememberMe { get; set; }
        }

        public async Task OnGetAsync( string returnUrl = null )
        {
            if ( !string.IsNullOrEmpty( this.ErrorMessage ) )
                this.ModelState.AddModelError( string.Empty, this.ErrorMessage );

            returnUrl ??= this.Url.Content( "~/" );

            // Clear the existing external cookie to ensure a clean login process
            await this.HttpContext.SignOutAsync( IdentityConstants.ExternalScheme ).ConfigureAwait( false );

            this.ExternalLogins =
                ( await this.signInManager.GetExternalAuthenticationSchemesAsync( ).ConfigureAwait( false ) ).ToList( );

            this.ReturnUrl = returnUrl;
        }

        public async Task<IActionResult> OnPostAsync( string returnUrl = null )
        {
            returnUrl = returnUrl ?? this.Url.Content( "~/" );

            if ( this.ModelState.IsValid )
            {
                // This doesn't count login failures towards account lockout
                // To enable password failures to trigger account lockout, set lockoutOnFailure: true
                SignInResult result = await this.signInManager
                                                .PasswordSignInAsync(
                                                                     this.Input.Email,
                                                                     this.Input.Password,
                                                                     this.Input.RememberMe,
                                                                     false )
                                                .ConfigureAwait( false );

                if ( result.Succeeded )
                {
                    this.logger.LogInformation( "User logged in." );

                    return this.LocalRedirect( returnUrl );
                }

                if ( result.RequiresTwoFactor )
                {
                    return this.RedirectToPage(
                                               "./LoginWith2fa",
                                               new { ReturnUrl = returnUrl, this.Input.RememberMe } );
                }

                if ( result.IsLockedOut )
                {
                    this.logger.LogWarning( "User account locked out." );

                    return this.RedirectToPage( "./Lockout" );
                }

                this.ModelState.AddModelError( string.Empty, "Invalid login attempt." );

                return this.Page( );
            }

            // If we got this far, something failed, redisplay form
            return this.Page( );
        }
    }
}
