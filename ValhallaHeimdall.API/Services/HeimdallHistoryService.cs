using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Identity;
using ValhallaHeimdall.BLL.Models;
using ValhallaHeimdall.DAL.Data;

namespace ValhallaHeimdall.API.Services
{
    public class HeimdallHistoryService : IHeimdallHistoryService
    {
        private readonly ApplicationDbContext context;

        private readonly UserManager<HeimdallUser> userManager;

        public HeimdallHistoryService( ApplicationDbContext context, UserManager<HeimdallUser> userManager )
        {
            this.context     = context;
            this.userManager = userManager;
        }

        public async Task AddHistoryAsync( Ticket oldTicket, Ticket newTicket, string userId )
        {
            if ( oldTicket.Title != newTicket.Title )
            {
                TicketHistory history = new TicketHistory
                                        {
                                            TicketId = newTicket.Id,
                                            Property = "Title",
                                            OldValue = oldTicket.Title,
                                            NewValue = newTicket.Title,
                                            Created  = DateTimeOffset.Now,
                                            UserId   = userId
                                        };
                await this.context.TicketHistories.AddAsync( history ).ConfigureAwait( false );
            }

            if ( oldTicket.Description != newTicket.Description )
            {
                TicketHistory history = new TicketHistory
                                        {
                                            TicketId = newTicket.Id,
                                            Property = "Description",
                                            OldValue = oldTicket.Description,
                                            NewValue = newTicket.Description,
                                            Created  = DateTimeOffset.Now,
                                            UserId   = userId
                                        };
                await this.context.TicketHistories.AddAsync( history ).ConfigureAwait( false );
            }

            if ( oldTicket.TicketTypeId != newTicket.TicketTypeId )
            {
                TicketHistory history = new TicketHistory
                                        {
                                            TicketId = newTicket.Id,
                                            Property = "TicketTypeId",
                                            OldValue =
                                                ( await this.context.TicketTypes.FindAsync( oldTicket.TicketTypeId )
                                                            .ConfigureAwait( false ) ).Name,
                                            NewValue = ( await this.context.TicketTypes
                                                                   .FindAsync( newTicket.TicketTypeId )
                                                                   .ConfigureAwait( false ) ).Name,
                                            Created = DateTimeOffset.Now,
                                            UserId  = userId
                                        };
                await this.context.TicketHistories.AddAsync( history ).ConfigureAwait( false );
            }

            if ( oldTicket.TicketPriorityId != newTicket.TicketPriorityId )
            {
                TicketHistory history = new TicketHistory
                                        {
                                            TicketId = newTicket.Id,
                                            Property = "TicketPriorityId",
                                            OldValue =
                                                ( await this.context.TicketPriorities
                                                            .FindAsync( oldTicket.TicketPriorityId )
                                                            .ConfigureAwait( false ) ).Name,
                                            NewValue = ( await this.context.TicketPriorities
                                                                   .FindAsync( newTicket.TicketPriorityId )
                                                                   .ConfigureAwait( false ) ).Name,
                                            Created = DateTimeOffset.Now,
                                            UserId  = userId
                                        };
                await this.context.TicketHistories.AddAsync( history ).ConfigureAwait( false );
            }

            if ( oldTicket.TicketPriorityId != newTicket.TicketPriorityId )
            {
                TicketHistory history = new TicketHistory
                                        {
                                            TicketId = newTicket.Id,
                                            Property = "TicketStatusId",
                                            OldValue =
                                                ( await this.context.TicketStatuses
                                                            .FindAsync( oldTicket.TicketStatusId )
                                                            .ConfigureAwait( false ) ).Name,
                                            NewValue = ( await this.context.TicketStatuses
                                                                   .FindAsync( newTicket.TicketStatusId )
                                                                   .ConfigureAwait( false ) ).Name,
                                            Created = DateTimeOffset.Now,
                                            UserId  = userId
                                        };
                await this.context.TicketHistories.AddAsync( history ).ConfigureAwait( false );
            }

            if ( oldTicket.DeveloperUserId != newTicket.DeveloperUserId )
            {
                if ( string.IsNullOrWhiteSpace( oldTicket.DeveloperUserId ) )
                {
                    string oldValue = oldTicket.DeveloperUserId == null
                                          ? "Unassigned"
                                          : ( await this.context.Users.FindAsync( oldTicket.DeveloperUserId )
                                                        .ConfigureAwait( false ) ).FullName;
                    TicketHistory history = new TicketHistory
                                            {
                                                TicketId = newTicket.Id,
                                                Property = "DeveloperUserId",
                                                OldValue = "No Developer Assigned",
                                                NewValue =
                                                    ( await this.context.Users.FindAsync( newTicket.DeveloperUserId )
                                                                .ConfigureAwait( false ) ).FullName,
                                                Created = DateTimeOffset.Now,
                                                UserId  = userId
                                            };
                    await this.context.TicketHistories.AddAsync( history ).ConfigureAwait( false );
                }
            }
        }
    }
}
