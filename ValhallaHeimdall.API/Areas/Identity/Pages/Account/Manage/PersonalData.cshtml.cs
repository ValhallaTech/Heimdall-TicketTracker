using System.Threading.Tasks;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Logging;
using ValhallaHeimdall.BLL.Models;

namespace ValhallaHeimdall.API.Areas.Identity.Pages.Account.Manage
{
    public class PersonalDataModel : PageModel
    {
        private readonly UserManager<HeimdallUser> userManager;

        private readonly ILogger<PersonalDataModel> logger;

        public PersonalDataModel( UserManager<HeimdallUser> userManager, ILogger<PersonalDataModel> logger )
        {
            this.userManager = userManager;
            this.logger      = logger;
        }

        public async Task<IActionResult> OnGet( )
        {
            HeimdallUser user = await this.userManager.GetUserAsync( this.User ).ConfigureAwait( false );

            if ( user == null )
                return this.NotFound( $"Unable to load user with ID '{this.userManager.GetUserId( this.User )}'." );

            return this.Page( );
        }
    }
}
