using System.ComponentModel.DataAnnotations;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using ValhallaHeimdall.BLL.Models;

namespace ValhallaHeimdall.API.Areas.Identity.Pages.Account.Manage
{
    public class SetPasswordModel : PageModel
    {
        private readonly UserManager<HeimdallUser> userManager;

        private readonly SignInManager<HeimdallUser> signInManager;

        public SetPasswordModel( UserManager<HeimdallUser> userManager, SignInManager<HeimdallUser> signInManager )
        {
            this.userManager   = userManager;
            this.signInManager = signInManager;
        }

        [BindProperty]
        public InputModel Input { get; set; }

        [TempData]
        public string StatusMessage { get; set; }

        public class InputModel
        {
            [Required]
            [StringLength(
                             100,
                             ErrorMessage  = "The {0} must be at least {2} and at max {1} characters long.",
                             MinimumLength = 6 )]
            [DataType( DataType.Password )]
            [Display( Name = "New password" )]
            public string NewPassword { get; set; }

            [DataType( DataType.Password )]
            [Display( Name                        = "Confirm new password" )]
            [Compare( "NewPassword", ErrorMessage = "The new password and confirmation password do not match." )]
            public string ConfirmPassword { get; set; }
        }

        public async Task<IActionResult> OnGetAsync( )
        {
            HeimdallUser user = await this.userManager.GetUserAsync( this.User ).ConfigureAwait( false );

            if ( user == null )
                return this.NotFound( $"Unable to load user with ID '{this.userManager.GetUserId( this.User )}'." );

            bool hasPassword = await this.userManager.HasPasswordAsync( user ).ConfigureAwait( false );

            if ( hasPassword ) return this.RedirectToPage( "./ChangePassword" );

            return this.Page( );
        }

        public async Task<IActionResult> OnPostAsync( )
        {
            if ( !this.ModelState.IsValid ) return this.Page( );

            HeimdallUser user = await this.userManager.GetUserAsync( this.User ).ConfigureAwait( false );

            if ( user == null )
                return this.NotFound( $"Unable to load user with ID '{this.userManager.GetUserId( this.User )}'." );

            IdentityResult addPasswordResult = await this.userManager.AddPasswordAsync( user, this.Input.NewPassword )
                                                         .ConfigureAwait( false );

            if ( !addPasswordResult.Succeeded )
            {
                foreach ( IdentityError error in addPasswordResult.Errors )
                    this.ModelState.AddModelError( string.Empty, error.Description );

                return this.Page( );
            }

            await this.signInManager.RefreshSignInAsync( user ).ConfigureAwait( false );
            this.StatusMessage = "Your password has been set.";

            return this.RedirectToPage( );
        }
    }
}
