using System;
using System.ComponentModel.DataAnnotations;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Logging;
using ValhallaHeimdall.BLL.Models;

namespace ValhallaHeimdall.API.Areas.Identity.Pages.Account.Manage
{
    public class DeletePersonalDataModel : PageModel
    {
        private readonly UserManager<HeimdallUser> userManager;

        private readonly SignInManager<HeimdallUser> signInManager;

        private readonly ILogger<DeletePersonalDataModel> logger;

        public DeletePersonalDataModel(
            UserManager<HeimdallUser>        userManager,
            SignInManager<HeimdallUser>      signInManager,
            ILogger<DeletePersonalDataModel> logger )
        {
            this.userManager   = userManager;
            this.signInManager = signInManager;
            this.logger        = logger;
        }

        [BindProperty]
        public InputModel Input { get; set; }

        public class InputModel
        {
            [Required]
            [DataType( DataType.Password )]
            public string Password { get; set; }
        }

        public bool RequirePassword { get; set; }

        public async Task<IActionResult> OnGet( )
        {
            HeimdallUser user = await this.userManager.GetUserAsync( this.User ).ConfigureAwait( false );

            if ( user == null )
                return this.NotFound( $"Unable to load user with ID '{this.userManager.GetUserId( this.User )}'." );

            this.RequirePassword = await this.userManager.HasPasswordAsync( user ).ConfigureAwait( false );

            return this.Page( );
        }

        public async Task<IActionResult> OnPostAsync( )
        {
            HeimdallUser user = await this.userManager.GetUserAsync( this.User ).ConfigureAwait( false );

            if ( user == null )
                return this.NotFound( $"Unable to load user with ID '{this.userManager.GetUserId( this.User )}'." );

            this.RequirePassword = await this.userManager.HasPasswordAsync( user ).ConfigureAwait( false );

            if ( this.RequirePassword )
            {
                if ( !await this.userManager.CheckPasswordAsync( user, this.Input.Password ).ConfigureAwait( false ) )
                {
                    this.ModelState.AddModelError( string.Empty, "Incorrect password." );

                    return this.Page( );
                }
            }

            IdentityResult result = await this.userManager.DeleteAsync( user ).ConfigureAwait( false );
            string         userId = await this.userManager.GetUserIdAsync( user ).ConfigureAwait( false );

            if ( !result.Succeeded )
                throw new InvalidOperationException( $"Unexpected error occurred deleting user with ID '{userId}'." );

            await this.signInManager.SignOutAsync( ).ConfigureAwait( false );

            this.logger.LogInformation( "User with ID '{UserId}' deleted themselves.", userId );

            return this.Redirect( "~/" );
        }
    }
}
