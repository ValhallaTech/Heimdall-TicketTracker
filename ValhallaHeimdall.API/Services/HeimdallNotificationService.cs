using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Identity.UI.Services;
using Microsoft.EntityFrameworkCore;
using ValhallaHeimdall.BLL.Models;
using ValhallaHeimdall.DAL.Data;

namespace ValhallaHeimdall.API.Services
{
    public class HeimdallNotificationService : IHeimdallNotificationService
    {
        private readonly ApplicationDbContext context;

        private readonly IEmailSender emailService;

        public HeimdallNotificationService( ApplicationDbContext context, IEmailSender emailService )
        {
            this.context      = context;
            this.emailService = emailService;
        }

        public async Task NotifyAsync( string userId, Ticket ticket, TicketHistory change )
        {
            Notification notification = new Notification
                                        {
                                            TicketId = ticket.Id,
                                            Description =
                                                $"The {change.Property} was updated from {change.OldValue} to {change.NewValue}.",
                                            Created     = DateTime.Now,
                                            SenderId    = userId,
                                            RecipientId = ticket.DeveloperUserId
                                        };
            await this.context.Notifications.AddAsync( notification ).ConfigureAwait( false );
            await this.context.SaveChangesAsync( ).ConfigureAwait( false );
            string to = ticket.DeveloperUser.Email;
            string subject =
                $"For project: {ticket.Project.Name}, ticket: {ticket.Title}, priority: {ticket.TicketPriority.Name}";
            await this.emailService.SendEmailAsync( to, subject, notification.Description ).ConfigureAwait( false );
        }

        public async Task NotifyOfCommentAsync( string userId, Ticket ticket, TicketComment comment )
        {
            HeimdallUser user =
                await this.context.Users.FirstOrDefaultAsync( u => u.Id == userId ).ConfigureAwait( false );
            Notification notification = new Notification
                                        {
                                            TicketId = ticket.Id,
                                            Description =
                                                $"{user.FullName} left a comment on Ticket titled: '{ticket.Title}' saying, '{comment.Comment}'",
                                            Created     = DateTime.Now,
                                            SenderId    = userId,
                                            RecipientId = ticket.DeveloperUserId
                                        };
            await this.context.Notifications.AddAsync( notification ).ConfigureAwait( false );
            await this.context.SaveChangesAsync( ).ConfigureAwait( false );
            string to = ticket.DeveloperUser.Email;
            string subject =
                $"For project: {ticket.Project.Name}, ticket: {ticket.Title}, priority: {ticket.TicketPriority.Name}";
            await this.emailService.SendEmailAsync( to, subject, notification.Description ).ConfigureAwait( false );
        }

        public async Task NotifyOfAttachmentAsync( string userId, Ticket ticket, TicketAttachment attachment )
        {
            HeimdallUser user =
                await this.context.Users.FirstOrDefaultAsync( u => u.Id == userId ).ConfigureAwait( false );
            Notification notification = new Notification
                                        {
                                            TicketId = ticket.Id,
                                            Description =
                                                $"{user.FullName} added an attachment on Ticket titled: '{ticket.Title}', named, '{attachment.Description}'",
                                            Created     = DateTime.Now,
                                            SenderId    = userId,
                                            RecipientId = ticket.DeveloperUserId
                                        };
            await this.context.Notifications.AddAsync( notification ).ConfigureAwait( false );
            await this.context.SaveChangesAsync( ).ConfigureAwait( false );
            string to = ticket.DeveloperUser.Email;
            string subject =
                $"For project: {ticket.Project.Name}, ticket: {ticket.Title}, priority: {ticket.TicketPriority.Name}";
            await this.emailService.SendEmailAsync( to, subject, notification.Description ).ConfigureAwait( false );
        }
    }
}
