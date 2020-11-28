using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Logging;
using ValhallaHeimdall.BLL.Models;

namespace ValhallaHeimdall.API.Areas.Identity.Pages.Account
{
    [AllowAnonymous]
    public class LogoutModel : PageModel
    {
        private readonly SignInManager<HeimdallUser> signInManager;

        private readonly ILogger<LogoutModel> logger;

        public LogoutModel( SignInManager<HeimdallUser> signInManager, ILogger<LogoutModel> logger )
        {
            this.signInManager = signInManager;
            this.logger        = logger;
        }

        public void OnGet( )
        {
        }

        public async Task<IActionResult> OnPost( string returnUrl = null )
        {
            await this.signInManager.SignOutAsync( ).ConfigureAwait( false );
            this.logger.LogInformation( "User logged out." );

            if ( returnUrl != null ) return this.LocalRedirect( returnUrl );

            return this.RedirectToPage( );
        }
    }
}
