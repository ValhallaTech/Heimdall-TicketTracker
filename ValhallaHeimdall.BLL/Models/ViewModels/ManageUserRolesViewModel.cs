using System.Collections.Generic;
using Microsoft.AspNetCore.Mvc.Rendering;

namespace ValhallaHeimdall.BLL.Models.ViewModels
{
    public class ManageUserRolesViewModel
    {
        public HeimdallUser User { get; set; }

        public MultiSelectList Roles { get; set; }

        public IEnumerable<string> UserRole { get; set; }

        public string[] SelectedRoles { get; set; }
    }
}
