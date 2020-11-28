using Microsoft.AspNetCore.Mvc.Rendering;

namespace ValhallaHeimdall.BLL.Models.ViewModels
{
    public class ProjectUsersViewModel
    {
        public Project Project { get; set; }

        public MultiSelectList Users { get; set; }

        public string[] SelectedUsers { get; set; }
    }
}
