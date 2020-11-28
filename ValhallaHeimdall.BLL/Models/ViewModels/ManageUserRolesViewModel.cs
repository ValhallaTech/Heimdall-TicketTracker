using Microsoft.AspNetCore.Mvc.Rendering;

namespace ValhallaHeimdall.BLL.Models.ViewModels
{
    public class ManageUserRolesViewModel
    {
        public HeimdallUser User { get; set; }

        public MultiSelectList Roles { get; set; }

        public string[] SelectedRoles { get; set; }
    }
}
