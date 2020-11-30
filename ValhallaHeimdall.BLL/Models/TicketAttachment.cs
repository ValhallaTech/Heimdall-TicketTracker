using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.AspNetCore.Http;
using ValhallaHeimdall.API.Extensions;

namespace ValhallaHeimdall.BLL.Models
{
    public class TicketAttachment
    {
        // public int Id { get; set; }
        // public string FilePath { get; set; }
        public int Id { get; set; }

        public string FilePath { get; set; }

        [Display( Name = "Select File" )]
        [NotMapped]
        [DataType( DataType.Upload )]
        [MaxFileSize( 2 * 1024 * 1024 )]
        [AllowedExtensions( new[] { ".jpg", ".png", ".doc", ".docx", ".xls", ".xlsx", ".pdf" } )]
        public IFormFile FormFile { get; set; }

        public string FileName { get; set; }

        [Required]
        public byte[] FileData { get; set; }

        public string Description { get; set; }

        public DateTimeOffset Created { get; set; }

        public int TicketId { get; set; }

        public virtual Ticket Ticket { get; set; }

        public string UserId { get; set; }

        public virtual HeimdallUser User { get; set; }
    }
}
