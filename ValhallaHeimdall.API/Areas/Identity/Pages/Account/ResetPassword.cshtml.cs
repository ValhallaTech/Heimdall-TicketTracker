using System.ComponentModel.DataAnnotations;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.WebUtilities;
using ValhallaHeimdall.BLL.Models;

namespace ValhallaHeimdall.API.Areas.Identity.Pages.Account
{
    [AllowAnonymous]
    public class ResetPasswordModel : PageModel
    {
        private readonly UserManager<HeimdallUser> userManager;

        public ResetPasswordModel( UserManager<HeimdallUser> userManager ) => this.userManager = userManager;

        [BindProperty]
        public InputModel Input { get; set; }

        public class InputModel
        {
            [Required]
            [EmailAddress]
            public string Email { get; set; }

            [Required]
            [StringLength(
                             100,
                             ErrorMessage  = "The {0} must be at least {2} and at max {1} characters long.",
                             MinimumLength = 6 )]
            [DataType( DataType.Password )]
            public string Password { get; set; }

            [DataType( DataType.Password )]
            [Display( Name                     = "Confirm password" )]
            [Compare( "Password", ErrorMessage = "The password and confirmation password do not match." )]
            public string ConfirmPassword { get; set; }

            public string Code { get; set; }
        }

        public IActionResult OnGet( string code = null )
        {
            if ( code == null )
            {
                return this.BadRequest( "A code must be supplied for password reset." );
            }

            this.Input = new InputModel { Code = Encoding.UTF8.GetString( WebEncoders.Base64UrlDecode( code ) ) };

            return this.Page( );
        }

        public async Task<IActionResult> OnPostAsync( )
        {
            if ( !this.ModelState.IsValid ) return this.Page( );

            HeimdallUser user = await this.userManager.FindByEmailAsync( this.Input.Email ).ConfigureAwait( false );

            if ( user == null )
            {
                // Don't reveal that the user does not exist
                return this.RedirectToPage( "./ResetPasswordConfirmation" );
            }

            IdentityResult result = await this.userManager
                                              .ResetPasswordAsync( user, this.Input.Code, this.Input.Password )
                                              .ConfigureAwait( false );

            if ( result.Succeeded ) return this.RedirectToPage( "./ResetPasswordConfirmation" );

            foreach ( IdentityError error in result.Errors )
                this.ModelState.AddModelError( string.Empty, error.Description );

            return this.Page( );
        }
    }
}
