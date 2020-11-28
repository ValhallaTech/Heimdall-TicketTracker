namespace ValhallaHeimdall.BLL.Models
{
    public class ProjectUser
    {
        public string UserId { get; set; }

        public HeimdallUser User { get; set; }

        public int ProjectId { get; set; }

        public Project Project { get; set; }

        public string AddProjectUsers { get; set; }

        public string RemoveProjectUsers { get; set; }
    }
}
