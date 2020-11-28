using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.AspNetCore.Identity;

namespace ValhallaHeimdall.Models
{
    public class HeimdallUser : IdentityUser
    {
        [Required]
        [StringLength( 50 )]
        public string FirstName { get; set; }

        [Required]
        [StringLength( 50 )]
        public string LastName { get; set; }

        [Display( Name = "Full Name" )]
        [NotMapped]
        public string FullName => $"{this.FirstName} {this.LastName}";

        [Display( Name = "Avatar" )]
        public string ImagePath { get; set; }

        public byte[] ImageData { get; set; }

        public List<ProjectUser> ProjectUsers { get; set; }
    }
}
