using System;
using System.IO;
using Microsoft.AspNetCore.Http;
using ValhallaHeimdall.BLL.Models;

namespace ValhallaHeimdall.API.Utilities
{
    public class AttachmentHandler
    {
        public TicketAttachment Attach( IFormFile attachment, int ticketId )
        {
            TicketAttachment ticketAttachment = new TicketAttachment();
            MemoryStream memoryStream = new MemoryStream();
            attachment.CopyTo(memoryStream);
            byte[] bytes = memoryStream.ToArray();
            memoryStream.Close();
            memoryStream.Dispose();
            string binary = Convert.ToBase64String(bytes);
            string? ext = Path.GetExtension(attachment.FileName);
            ticketAttachment.TicketId    = ticketId;
            ticketAttachment.FilePath    = $"data:image/{ext};base64,{binary}";
            ticketAttachment.FileData    = bytes;
            ticketAttachment.Description = Path.GetFileNameWithoutExtension(attachment.FileName.Replace( " ", "_" ) );
            ticketAttachment.Created     = DateTime.Now;

            return ticketAttachment;
        }
    }
}
