using System.Collections.Generic;
using Microsoft.AspNetCore.Mvc.Rendering;

namespace ValhallaHeimdall.BLL.Models.ViewModels
{
    public class ManageProjectUsersViewModel
    {
        public Project Project { get; set; }

        public MultiSelectList Users { get; set; }

        public string[] SelectedUsers { get; set; }

        public MultiSelectList MultiSelectUsersOnProject { get; set; }

        public MultiSelectList MultiSelectUsersOffProject { get; set; }

        public string[] SelectedUsersOnProject { get; set; }

        public string[] SelectedUsersOffProject { get; set; }

        public List<HeimdallUser> UsersOnProject { get; set; }

        public List<HeimdallUser> UsersOffProject { get; set; }
    }
}
