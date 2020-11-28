using System.Threading.Tasks;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Logging;
using ValhallaHeimdall.BLL.Models;

namespace ValhallaHeimdall.API.Areas.Identity.Pages.Account.Manage
{
    public class TwoFactorAuthenticationModel : PageModel
    {
        private const string AuthenicatorUriFormat = "otpauth://totp/{0}:{1}?secret={2}&issuer={0}";

        private readonly UserManager<HeimdallUser> userManager;

        private readonly SignInManager<HeimdallUser> signInManager;

        private readonly ILogger<TwoFactorAuthenticationModel> logger;

        public TwoFactorAuthenticationModel(
            UserManager<HeimdallUser>             userManager,
            SignInManager<HeimdallUser>           signInManager,
            ILogger<TwoFactorAuthenticationModel> logger )
        {
            this.userManager   = userManager;
            this.signInManager = signInManager;
            this.logger        = logger;
        }

        public bool HasAuthenticator { get; set; }

        public int RecoveryCodesLeft { get; set; }

        [BindProperty]
        public bool Is2FaEnabled { get; set; }

        public bool IsMachineRemembered { get; set; }

        [TempData]
        public string StatusMessage { get; set; }

        public async Task<IActionResult> OnGet( )
        {
            HeimdallUser user = await this.userManager.GetUserAsync( this.User ).ConfigureAwait( false );

            if ( user == null )
                return this.NotFound( $"Unable to load user with ID '{this.userManager.GetUserId( this.User )}'." );

            this.HasAuthenticator =
                await this.userManager.GetAuthenticatorKeyAsync( user ).ConfigureAwait( false ) != null;
            this.Is2FaEnabled = await this.userManager.GetTwoFactorEnabledAsync( user ).ConfigureAwait( false );
            this.IsMachineRemembered =
                await this.signInManager.IsTwoFactorClientRememberedAsync( user ).ConfigureAwait( false );
            this.RecoveryCodesLeft = await this.userManager.CountRecoveryCodesAsync( user ).ConfigureAwait( false );

            return this.Page( );
        }

        public async Task<IActionResult> OnPost( )
        {
            HeimdallUser user = await this.userManager.GetUserAsync( this.User ).ConfigureAwait( false );

            if ( user == null )
                return this.NotFound( $"Unable to load user with ID '{this.userManager.GetUserId( this.User )}'." );

            await this.signInManager.ForgetTwoFactorClientAsync( ).ConfigureAwait( false );
            this.StatusMessage =
                "The current browser has been forgotten. When you login again from this browser you will be prompted for your 2fa code.";

            return this.RedirectToPage( );
        }
    }
}
