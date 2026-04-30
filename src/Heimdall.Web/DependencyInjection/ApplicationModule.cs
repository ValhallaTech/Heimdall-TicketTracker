using System;
using System.Reflection;
using Autofac;
using AutoMapper.Contrib.Autofac.DependencyInjection;
using Heimdall.BLL.Mapping;
using Heimdall.BLL.Services;
using Heimdall.Core.Interfaces;
using Heimdall.DAL.Caching;
using Heimdall.DAL.Repositories;

namespace Heimdall.Web.DependencyInjection;

/// <summary>
/// Registers the BLL, DAL, and AutoMapper components into the Autofac container.
/// </summary>
public class ApplicationModule : Autofac.Module
{
    /// <inheritdoc />
    protected override void Load(ContainerBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        // Repositories
        builder
            .RegisterType<TicketRepository>()
            .As<ITicketRepository>()
            .InstancePerLifetimeScope();

        // Caching
        builder.RegisterType<RedisCacheService>().As<ICacheService>().SingleInstance();

        // Services
        builder
            .RegisterType<TicketService>()
            .As<ITicketService>()
            .InstancePerLifetimeScope();

        // AutoMapper profiles (BLL assembly)
        builder.RegisterAutoMapper(typeof(TicketProfile).Assembly);
    }
}
