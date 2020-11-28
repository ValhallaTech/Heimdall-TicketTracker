//using System.Linq;
//using System.Reflection;
//using Autofac;
//using AutoMapper.Contrib.Autofac.DependencyInjection;
//using ValhallaHeimdall.API.Services;

//namespace ValhallaHeimdall.API.Utilities
//{
//    public static class ContainerConfig
//    {
//        public static IContainer Configure( )
//        {
//            ContainerBuilder builder = new ContainerBuilder( );

//            builder.RegisterType<HeimdallAccessService>( ).As<IHeimdallAccessService>( );

//            builder.RegisterAssemblyTypes( Assembly.Load( nameof( Services ) ) )
//                   .Where( t => t.Namespace.Contains( "Services" ) )
//                   .As( t => System.Array.Find( t.GetInterfaces( ), i => i.Name == "I" + t.Name ) );

//            return builder.Build( );
//        }
//    }
//}
