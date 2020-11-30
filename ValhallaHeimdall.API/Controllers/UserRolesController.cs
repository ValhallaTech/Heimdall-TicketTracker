using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using ValhallaHeimdall.API.Services;
using ValhallaHeimdall.BLL.Models;
using ValhallaHeimdall.BLL.Models.ViewModels;
using ValhallaHeimdall.DAL.Data;

namespace ValhallaHeimdall.API.Controllers
{
    [Authorize]
    public class UserRolesController : Controller
    {
        private readonly ApplicationDbContext context;

        public IHeimdallRolesService RolesService;

        private readonly UserManager<HeimdallUser> userManager;

        public UserRolesController(
            ApplicationDbContext      context,
            IHeimdallRolesService     rolesService,
            UserManager<HeimdallUser> userManager )
        {
            this.userManager  = userManager;
            this.context      = context;
            this.RolesService = rolesService;
            this.userManager  = userManager;
        }

        [HttpGet]
        public async Task<IActionResult> ManageUserRoles( )
        {
            List<ManageUserRolesViewModel> model = new List<ManageUserRolesViewModel>( );
            List<HeimdallUser>             users = this.context.Users.ToList( );

            foreach ( HeimdallUser user in users )
            {
                ManageUserRolesViewModel vm = new ManageUserRolesViewModel { User = user };

                IEnumerable<string> selected =
                    await this.RolesService.ListUserRolesAsync( user ).ConfigureAwait( false );

                vm.Roles    = new MultiSelectList( this.context.Roles, "Name", "Name", selected );
                vm.UserRole = await this.RolesService.ListUserRolesAsync( user ).ConfigureAwait( false );
                model.Add( vm );
            }

            return this.View( model );
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ManageUserRoles( ManageUserRolesViewModel heimdallUser )
        {
            if ( !this.User.IsInRole( "DemoUser" ) )
            {
                HeimdallUser user = await this.context.Users.FindAsync( heimdallUser.User.Id ).ConfigureAwait( false );

                IEnumerable<string> roles = await this.RolesService.ListUserRolesAsync( user ).ConfigureAwait( false );
                await this.userManager.RemoveFromRolesAsync( user, roles ).ConfigureAwait( false );
                string[] userRoles = heimdallUser.SelectedRoles;

                // string userRole = HeimdallUser.SelectedRoles.FirstOrDefault();
                foreach ( string role in userRoles )
                {
                    if ( Enum.TryParse( role, out Roles roleValue ) )
                    {
                        await this.RolesService.AddUserToRoleAsync( user, role ).ConfigureAwait( false );
                    }
                }

                return this.RedirectToAction( "ManageUserRoles" );
            }
            else
            {
                this.TempData["DemoLockout"] =
                    "Your changes have not been saved. To make changes to the database, please log in as a full user.";

                return this.RedirectToAction( "Index", "Home" );
            }
        }
    }
}
