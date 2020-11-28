﻿using System.ComponentModel.DataAnnotations;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Logging;
using ValhallaHeimdall.BLL.Models;

namespace ValhallaHeimdall.API.Areas.Identity.Pages.Account.Manage
{
    public class ChangePasswordModel : PageModel
    {
        private readonly UserManager<HeimdallUser> userManager;

        private readonly SignInManager<HeimdallUser> signInManager;

        private readonly ILogger<ChangePasswordModel> logger;

        public ChangePasswordModel(
            UserManager<HeimdallUser>    userManager,
            SignInManager<HeimdallUser>  signInManager,
            ILogger<ChangePasswordModel> logger )
        {
            this.userManager   = userManager;
            this.signInManager = signInManager;
            this.logger        = logger;
        }

        [BindProperty]
        public InputModel Input { get; set; }

        [TempData]
        public string StatusMessage { get; set; }

        public class InputModel
        {
            [Required]
            [DataType( DataType.Password )]
            [Display( Name = "Current password" )]
            public string OldPassword { get; set; }

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

            if ( !hasPassword ) return this.RedirectToPage( "./SetPassword" );

            return this.Page( );
        }

        public async Task<IActionResult> OnPostAsync( )
        {
            if ( !this.ModelState.IsValid ) return this.Page( );

            HeimdallUser user = await this.userManager.GetUserAsync( this.User ).ConfigureAwait( false );

            if ( user == null )
                return this.NotFound( $"Unable to load user with ID '{this.userManager.GetUserId( this.User )}'." );

            IdentityResult changePasswordResult = await this.userManager
                                                            .ChangePasswordAsync(
                                                             user,
                                                             this.Input.OldPassword,
                                                             this.Input.NewPassword )
                                                            .ConfigureAwait( false );

            if ( !changePasswordResult.Succeeded )
            {
                foreach ( IdentityError error in changePasswordResult.Errors )
                    this.ModelState.AddModelError( string.Empty, error.Description );

                return this.Page( );
            }

            await this.signInManager.RefreshSignInAsync( user ).ConfigureAwait( false );
            this.logger.LogInformation( "User changed their password successfully." );
            this.StatusMessage = "Your password has been changed.";

            return this.RedirectToPage( );
        }
    }
}
