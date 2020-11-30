using System;
using System.Threading;
using System.Threading.Tasks;
using MailKit;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.AspNetCore.Identity.UI.Services;
using Microsoft.Extensions.Options;
using MimeKit;
using ValhallaHeimdall.BLL.Models;

namespace ValhallaHeimdall.API.Services
{
    public class HeimdallEmailService : IEmailSender
    {
        public readonly MailSettings MailSettings;

        public HeimdallEmailService( IOptions<MailSettings> mailOptions, MailSettings mailSettings )
        {
            this.MailSettings = mailSettings;
        }

        public async Task SendEmailAsync( string emailTo, string subject, string htmlMessage )
        {
            MimeMessage email = new MimeMessage { Sender = MailboxAddress.Parse( this.MailSettings.Mail ) };
            email.To.Add( MailboxAddress.Parse( emailTo ) );
            email.Subject = subject;
            BodyBuilder builder = new BodyBuilder { HtmlBody = htmlMessage };
            email.Body = builder.ToMessageBody( );

            using SmtpClient smtp = new SmtpClient( );
            await smtp.ConnectAsync( this.MailSettings.Host, this.MailSettings.Port, SecureSocketOptions.StartTls, CancellationToken.None ).ConfigureAwait( false );

            try
            {
                await smtp.AuthenticateAsync( this.MailSettings.Mail, this.MailSettings.Password, CancellationToken.None )
                          .ConfigureAwait( false );
            }
            catch ( OperationCanceledException OperationCanceledException )
            {
                // TODO: Handle the System.OperationCanceledException
            }

            try
            {
                await smtp.SendAsync( email ).ConfigureAwait( false );
            }
            catch ( ProtocolException ProtocolException )
            {
                // TODO: Handle the MailKit.ProtocolException
            }

            await smtp.DisconnectAsync( true ).ConfigureAwait( false );
        }
    }
}
