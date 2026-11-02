using Microsoft.Extensions.DependencyInjection;
using Orleans;
using Orleans.Hosting;
using Orleans.Storage;
using CoCern.Core;
using CoCern.Domain;

namespace CoCern.Storage;

public static class SqliteSiloBuilderExtensions
{
    public static ISiloBuilder AddCoCernSqlite(this ISiloBuilder builder, string databasePath)
    {
        builder.Services.AddSingleton(services => new SqliteStore(databasePath) { Logger = Microsoft.Extensions.Logging.LoggerFactoryExtensions.CreateLogger<SqliteStore>(services.GetService<Microsoft.Extensions.Logging.ILoggerFactory>() ?? Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance) });
        builder.Services.AddSingleton<IDueWorkSource>(services => services.GetRequiredService<SqliteStore>());
        builder.Services.AddSingleton<IActivitySink>(services => services.GetRequiredService<SqliteStore>());
        builder.Services.AddSingleton<IFullTextSearch>(services => services.GetRequiredService<SqliteStore>());
        builder.Services.AddSingleton<ITaskArchive>(services => services.GetRequiredService<SqliteStore>());
        builder.Services.AddSingleton<IFeedbackRuleStore>(services => services.GetRequiredService<SqliteStore>());
        builder.Services.AddSingleton<IActivityCounterSource>(services => services.GetRequiredService<SqliteStore>());
        builder.Services.AddKeyedSingleton<IGrainStorage>("relay", (services, _) => new SqliteGrainStorage(services.GetRequiredService<SqliteStore>()));
        builder.AddStartupTask((services, cancellationToken) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            _ = services.GetRequiredService<SqliteStore>();
            return Task.CompletedTask;
        }, ServiceLifecycleStage.RuntimeInitialize);
        return builder;
    }
}
