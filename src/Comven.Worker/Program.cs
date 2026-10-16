using Comven.Infrastructure;
using Comven.Infrastructure.Data;
using Comven.Worker;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddHostedService<Worker>();

var host = builder.Build();
await using (var scope = host.Services.CreateAsyncScope())
{
    var schemaInitializer = scope.ServiceProvider.GetRequiredService<PostgresSchemaInitializer>();
    await schemaInitializer.EnsureInitializedAsync(CancellationToken.None);
}

host.Run();
