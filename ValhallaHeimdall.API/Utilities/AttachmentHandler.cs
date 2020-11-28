using System;
using System.IO;
using Microsoft.AspNetCore.Http;
using ValhallaHeimdall.BLL.Models;

namespace ValhallaHeimdall.API.Utilities
{
    public class AttachmentHandler
    {
        public TicketAttachment Attach( IFormFile attachment )
        {
            TicketAttachment ticketAttachment = new TicketAttachment( );

            MemoryStream memoryStream = new MemoryStream( );
            attachment.CopyTo( memoryStream );
            byte[] bytes = memoryStream.ToArray( );
            memoryStream.Close( );
            memoryStream.Dispose( );
            string  binary = Convert.ToBase64String( bytes );
            string? ext    = Path.GetExtension( attachment.FileName );

            ticketAttachment.FileData = bytes;
            ticketAttachment.Created  = DateTime.Now;

            return ticketAttachment;
        }
    }
}
