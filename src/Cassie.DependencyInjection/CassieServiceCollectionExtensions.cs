using Cntryl.Cassie.Connection;
using Cntryl.Cassie.Rest;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using System.Data.Common;

namespace Cntryl.Cassie.DependencyInjection
{
    /// <summary>Registers Cassie data sources and HTTP clients with Microsoft dependency injection.</summary>
    public static class CassieServiceCollectionExtensions
    {
        /// <summary>Adds a singleton data source and a scoped, initially closed connection when those services are missing.</summary>
        /// <remarks>Generic ADO.NET aliases are added only when no other provider registered them. Existing service lifetimes and ownership are preserved.</remarks>
        public static IServiceCollection AddCassieDataSource(this IServiceCollection services, string connectionString)
        {
            ArgumentNullException.ThrowIfNull(services);
            ArgumentNullException.ThrowIfNull(connectionString);

            services.TryAddSingleton(provider => new CassieDataSource(connectionString)
            {
                LoggerFactory = provider.GetService<ILoggerFactory>()
            });
            services.TryAddScoped(provider => provider.GetRequiredService<CassieDataSource>().CreateConnection());
            TryAddAlias<DbDataSource, CassieDataSource>(services, static source => source);
            TryAddAlias<DbConnection, CassieConnection>(services, static connection => connection);
            return services;
        }

        /// <summary>Adds scoped REST and administration clients sharing one owned HTTP session per scope when those services are missing.</summary>
        /// <remarks>The URI constructor creates a separate cookie container for each scope. Authentication remains explicit through LoginAsync.</remarks>
        public static IServiceCollection AddCassieRestClient(this IServiceCollection services, Uri baseAddress)
        {
            ArgumentNullException.ThrowIfNull(services);
            ArgumentNullException.ThrowIfNull(baseAddress);

            services.TryAddScoped(_ => new CassieRestClient(baseAddress));
            TryAddAlias<CassieAdminClient, CassieRestClient>(services, static client => client.Admin);
            return services;
        }

        private static void TryAddAlias<TService, TRegistered>(IServiceCollection services, Func<TRegistered, TService> project)
            where TService : class
            where TRegistered : class
        {
            ServiceDescriptor registration = services.Last(static descriptor => !descriptor.IsKeyedService && descriptor.ServiceType == typeof(TRegistered));
            if (registration.ImplementationInstance is TRegistered instance)
            {
                // An instance registration remains caller-owned, including through its alias.
                services.TryAddSingleton(project(instance));
                return;
            }

            services.TryAdd(ServiceDescriptor.Describe(typeof(TService),
                provider => project(provider.GetRequiredService<TRegistered>()), registration.Lifetime));
        }
    }
}
