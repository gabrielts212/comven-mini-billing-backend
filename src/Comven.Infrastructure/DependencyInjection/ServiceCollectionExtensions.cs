using Comven.Application.Abstractions;
using Comven.Infrastructure.Configuration;
using Comven.Infrastructure.Data;
using Comven.Infrastructure.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Comven.Infrastructure;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<BillingOptions>(configuration.GetSection(BillingOptions.SectionName));

        services.AddSingleton<IBillingDbConnectionFactory, NpgsqlBillingDbConnectionFactory>();
        services.AddSingleton<PostgresSchemaInitializer>();
        services.AddSingleton<DapperBillingService>();
        services.AddSingleton<IBillingQueryService>(provider => provider.GetRequiredService<DapperBillingService>());
        services.AddSingleton<IBillingCommandService>(provider => provider.GetRequiredService<DapperBillingService>());
        services.AddSingleton<IInvoiceBillingService>(provider => provider.GetRequiredService<DapperBillingService>());

        return services;
    }
}