using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Logging;
using ValhallaHeimdall.BLL.Models;

namespace ValhallaHeimdall.API.Areas.Identity.Pages.Account.Manage
{
    public class EnableAuthenticatorModel : PageModel
    {
        private const string AuthenticatorUriFormat = "otpauth://totp/{0}:{1}?secret={2}&issuer={0}&digits=6";

        private readonly UserManager<HeimdallUser> userManager;

        private readonly ILogger<EnableAuthenticatorModel> logger;

        private readonly UrlEncoder urlEncoder;

        public EnableAuthenticatorModel(
            UserManager<HeimdallUser>         userManager,
            ILogger<EnableAuthenticatorModel> logger,
            UrlEncoder                        urlEncoder )
        {
            this.userManager = userManager;
            this.logger      = logger;
            this.urlEncoder  = urlEncoder;
        }

        public string SharedKey { get; set; }

        public string AuthenticatorUri { get; set; }

        [TempData]
        public string[] RecoveryCodes { get; set; }

        [TempData]
        public string StatusMessage { get; set; }

        [BindProperty]
        public InputModel Input { get; set; }

        public class InputModel
        {
            [Required]
            [StringLength(
                             7,
                             ErrorMessage  = "The {0} must be at least {2} and at max {1} characters long.",
                             MinimumLength = 6 )]
            [DataType( DataType.Text )]
            [Display( Name = "Verification Code" )]
            public string Code { get; set; }
        }

        public async Task<IActionResult> OnGetAsync( )
        {
            HeimdallUser user = await this.userManager.GetUserAsync( this.User ).ConfigureAwait( false );

            if ( user == null )
                return this.NotFound( $"Unable to load user with ID '{this.userManager.GetUserId( this.User )}'." );

            await this.LoadSharedKeyAndQrCodeUriAsync( user ).ConfigureAwait( false );

            return this.Page( );
        }

        public async Task<IActionResult> OnPostAsync( )
        {
            HeimdallUser user = await this.userManager.GetUserAsync( this.User ).ConfigureAwait( false );

            if ( user == null )
                return this.NotFound( $"Unable to load user with ID '{this.userManager.GetUserId( this.User )}'." );

            if ( !this.ModelState.IsValid )
            {
                await this.LoadSharedKeyAndQrCodeUriAsync( user ).ConfigureAwait( false );

                return this.Page( );
            }

            // Strip spaces and hypens
            string verificationCode = this.Input.Code.Replace( " ", string.Empty ).Replace( "-", string.Empty );

            bool is2FaTokenValid = await this.userManager
                                             .VerifyTwoFactorTokenAsync(
                                                                        user,
                                                                        this.userManager.Options.Tokens
                                                                            .AuthenticatorTokenProvider,
                                                                        verificationCode )
                                             .ConfigureAwait( false );

            if ( !is2FaTokenValid )
            {
                this.ModelState.AddModelError( "Input.Code", "Verification code is invalid." );
                await this.LoadSharedKeyAndQrCodeUriAsync( user ).ConfigureAwait( false );

                return this.Page( );
            }

            await this.userManager.SetTwoFactorEnabledAsync( user, true ).ConfigureAwait( false );
            string userId = await this.userManager.GetUserIdAsync( user ).ConfigureAwait( false );
            this.logger.LogInformation( "User with ID '{UserId}' has enabled 2FA with an authenticator app.", userId );

            this.StatusMessage = "Your authenticator app has been verified.";

            if ( await this.userManager.CountRecoveryCodesAsync( user ).ConfigureAwait( false ) == 0 )
            {
                IEnumerable<string> recoveryCodes = await this.userManager
                                                              .GenerateNewTwoFactorRecoveryCodesAsync( user, 10 )
                                                              .ConfigureAwait( false );
                this.RecoveryCodes = recoveryCodes.ToArray( );

                return this.RedirectToPage( "./ShowRecoveryCodes" );
            }

            return this.RedirectToPage( "./TwoFactorAuthentication" );
        }

        private async Task LoadSharedKeyAndQrCodeUriAsync( HeimdallUser user )
        {
            // Load the authenticator key & QR code URI to display on the form
            string unformattedKey = await this.userManager.GetAuthenticatorKeyAsync( user ).ConfigureAwait( false );

            if ( string.IsNullOrEmpty( unformattedKey ) )
            {
                await this.userManager.ResetAuthenticatorKeyAsync( user ).ConfigureAwait( false );
                unformattedKey = await this.userManager.GetAuthenticatorKeyAsync( user ).ConfigureAwait( false );
            }

            this.SharedKey = this.FormatKey( unformattedKey );

            string email = await this.userManager.GetEmailAsync( user ).ConfigureAwait( false );
            this.AuthenticatorUri = this.GenerateQrCodeUri( email, unformattedKey );
        }

        private string FormatKey( string unformattedKey )
        {
            StringBuilder result          = new StringBuilder( );
            int           currentPosition = 0;

            while ( currentPosition + 4 < unformattedKey.Length )
            {
                result.Append( unformattedKey.ToCharArray( currentPosition, 4 ) ).Append( string.Empty );
                currentPosition += 4;
            }

            if ( currentPosition < unformattedKey.Length ) result.Append( unformattedKey.Substring( currentPosition ) );

            return result.ToString( ).ToLowerInvariant( );
        }

        private string GenerateQrCodeUri( string email, string unformattedKey ) =>
            string.Format(
                          AuthenticatorUriFormat,
                          this.urlEncoder.Encode( "ValhallaHeimdall" ),
                          this.urlEncoder.Encode( email ),
                          unformattedKey );
    }
}
