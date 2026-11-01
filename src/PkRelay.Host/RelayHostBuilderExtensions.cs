using System.Net;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Orleans.Configuration;
using Orleans.Hosting;
using PkRelay.Analytics;
using PkRelay.Contracts;
using PkRelay.Core;
using PkRelay.Domain;
using PkRelay.Storage;
using Serilog;
using Serilog.Events;

namespace PkRelay.Host;

using Dashboard;

public static class RelayHostBuilderExtensions
{
    private const int DefaultSiloPort = 21111;
    private const int DefaultGatewayPort = 31111;
    private const string ServiceId = "PkRelay";

    public static WebApplicationBuilder AddRelayHost(this WebApplicationBuilder builder)
    {
        if (string.IsNullOrWhiteSpace(builder.Configuration["urls"])) builder.WebHost.UseUrls(HubNetwork.DefaultUrl);
        builder.WebHost.ConfigureKestrel((context, options) =>
        {
            options.Limits.MaxRequestBodySize = RelayLimits.MaxPayloadBytes;
            HubNetwork.Configure(context.Configuration, context.HostingEnvironment, options);
        });
        builder.Services.AddSerilog((services, configuration) => configuration.MinimumLevel.Information()
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .MinimumLevel.Override("Orleans", LogEventLevel.Warning)
            .MinimumLevel.Override("ModelContextProtocol", LogEventLevel.Warning)
            .WriteTo.Console().WriteTo.Sink(services.GetRequiredService<DashboardLogBuffer>()));
        // Host-level registration sees configuration added later by application factories and tests.
        builder.Host.ConfigureServices((context, services) => services.AddRelayAuthentication(context.Configuration));
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddSignalR(options => { options.EnableDetailedErrors = false; options.MaximumReceiveMessageSize = RelayLimits.MaxPayloadBytes; });
        builder.Services.AddSingleton<ICoordinationNotifier, CoordinationNotifier>();
        builder.Services.AddDiscovery();
        builder.Services.AddPkRelayCore();
        builder.Services.AddSingleton(services =>
        {
            var configuration = services.GetRequiredService<IConfiguration>();
            return new AttentionOptions
            {
                Enabled = configuration.GetValue("Relay:Attention:Enabled", true),
                MaxHoldMinutes = configuration.GetValue("Relay:Attention:MaxHoldMinutes", RelayLimits.DefaultAttentionMaxHoldMinutes),
                SnoozeBoundaries = configuration.GetValue("Relay:Attention:SnoozeBoundaries", RelayLimits.DefaultAttentionSnoozeBoundaries),
                ItemExpiryDays = configuration.GetValue("Relay:Attention:ItemExpiryDays", RelayLimits.DefaultAttentionExpiryDays)
            }.Validated();
        });
        builder.Services.AddSingleton(services => AnomalySettings(services.GetRequiredService<IConfiguration>()));
        // Standing orders are off by default; the board grains read the same options to decide whether orders run.
        builder.Services.AddSingleton(services =>
        {
            var configuration = services.GetRequiredService<IConfiguration>();
            return new OrderOptions
            {
                Enabled = configuration.GetValue("Relay:Orders:Enabled", false),
                DefaultExpiryDays = configuration.GetValue("Relay:Orders:DefaultExpiryDays", RelayLimits.DefaultOrderExpiryDays)
            }.Validated();
        });
        // The search port is passed only when the store keeps an index; otherwise the search tool answers SearchUnavailable. Likewise
        // analytics_query gets the analytics port only when analytics is on, and answers AnalyticsUnavailable otherwise.
        builder.Services.Replace(ServiceDescriptor.Singleton(services => new RelayApplication(
            services.GetRequiredService<Orleans.IGrainFactory>(), services.GetService<IMachineDirectory>(), services.GetService<IActivitySink>(),
            services.GetService<SqliteStore>() is { SearchEnabled: true } store ? store : null, services.GetRequiredService<AttentionOptions>(),
            services.GetService<IAnalyticsQueries>() is { } queries
                ? new AnalyticsTool(queries, services.GetRequiredService<IConfiguration>().GetValue("Relay:Analytics:AllowAdHocSql", false), services.GetRequiredService<ILoggerFactory>().CreateLogger<AnalyticsTool>())
                : null,
            // The anomaly monitor runs only with analytics on; its grain checks the same before every check.
            services.GetService<IAnalyticsQueries>() is not null ? services.GetRequiredService<AnomalyOptions>() : null,
            services.GetRequiredService<OrderOptions>())));
        builder.AddRelayOrleans();
        builder.Services.AddRelayDashboard();
        builder.Services.AddHostedService<DueWorkDispatcher>();
        builder.Services.AddRelayAnalytics();
        builder.Services.AddOpenApi();
        builder.Services.AddRelayMcp();
        return builder;
    }

    private static void AddRelayOrleans(this WebApplicationBuilder builder)
    {
        builder.UseOrleans(silo => silo.UseLocalhostClustering(siloPort: DefaultSiloPort, gatewayPort: DefaultGatewayPort, serviceId: ServiceId, clusterId: ServiceId)
            .AddPkRelaySqlite(DefaultDatabasePath(builder.Environment)));
        // Resolve final configuration through DI so application-factory and hosting overrides apply before any store/listener opens.
        builder.Services.Replace(ServiceDescriptor.Singleton<SqliteStore>(services =>
        {
            var environment = services.GetRequiredService<IHostEnvironment>();
            // A relative configured path resolves from the content root, so a launch profile can point at the repository's data folder.
            var configuration = services.GetRequiredService<IConfiguration>();
            var configured = configuration["Relay:DatabasePath"];
            var activity = new ActivityOptions
            {
                Enabled = configuration.GetValue("Relay:Activity:Enabled", true),
                RetentionDays = configuration.GetValue("Relay:Activity:RetentionDays", 30) is var days and >= 1 ? days : throw new InvalidOperationException("Relay:Activity:RetentionDays must be at least 1.")
            };
            var search = new SearchOptions { Enabled = configuration.GetValue("Relay:Search:Enabled", true) };
            return new SqliteStore(configured is null ? DefaultDatabasePath(environment) : Path.Combine(environment.ContentRootPath, configured), activity, search);
        }));
        builder.Services.AddOptions<EndpointOptions>().Configure<IConfiguration>((options, configuration) =>
        {
            options.SiloPort = configuration.GetValue("Relay:SiloPort", DefaultSiloPort);
            options.GatewayPort = configuration.GetValue("Relay:GatewayPort", DefaultGatewayPort);
            options.AdvertisedIPAddress = IPAddress.Loopback;
            options.SiloListeningEndpoint = new IPEndPoint(IPAddress.Loopback, options.SiloPort);
            options.GatewayListeningEndpoint = options.GatewayPort == 0 ? null : new IPEndPoint(IPAddress.Loopback, options.GatewayPort);
        });
        builder.Services.AddOptions<DevelopmentClusterMembershipOptions>().Configure<IConfiguration>((options, configuration) =>
            options.PrimarySiloEndpoint = new IPEndPoint(IPAddress.Loopback, configuration.GetValue("Relay:SiloPort", DefaultSiloPort)));
        builder.Services.AddOptions<ClusterOptions>().Configure<IConfiguration>((options, configuration) =>
        {
            options.ServiceId = ServiceId;
            options.ClusterId = configuration["Relay:ClusterId"] ?? ServiceId;
        });
    }

    /// <summary>
    /// Snapshots and DuckDB analytics, off by default. The analytics service is created only when enabled, so DuckDB's native library
    /// is not loaded otherwise; <see cref="IAnalyticsQueries"/> resolves to null when analytics is off.
    /// </summary>
    private static void AddRelayAnalytics(this IServiceCollection services)
    {
        services.AddSingleton(provider => AnalyticsSettings.From(provider.GetRequiredService<IConfiguration>(), provider.GetRequiredService<IHostEnvironment>(), provider.GetRequiredService<SqliteStore>()));
        services.AddSingleton(provider =>
        {
            var configuration = provider.GetRequiredService<IConfiguration>();
            return new AnalyticsService(new AnalyticsOptions
            {
                ExtensionDirectory = configuration["Relay:Analytics:ExtensionDirectory"] is { Length: > 0 } extensions ? Path.GetFullPath(Path.Combine(provider.GetRequiredService<IHostEnvironment>().ContentRootPath, extensions)) : AnalyticsOptions.DefaultExtensionDirectory,
                QueryTimeout = configuration.GetValue("Relay:Analytics:QueryTimeout", TimeSpan.FromSeconds(RelayLimits.DefaultAnalyticsQueryTimeoutSeconds)),
                MaxRows = configuration.GetValue("Relay:Analytics:MaxRows", RelayLimits.DefaultAnalyticsMaxRows),
                LiveDatabasePath = provider.GetRequiredService<SqliteStore>().DatabasePath
            });
        });
        services.AddSingleton<IAnalyticsQueries>(provider => provider.GetRequiredService<AnalyticsSettings>().Enabled ? provider.GetRequiredService<AnalyticsService>() : null!);
        services.AddHostedService<AnalyticsSnapshotService>();
    }

    /// <summary>
    /// <c>Relay:Attention:AnomalyInterval</c> (default 1 hour; 0 turns the monitor off) and per-query thresholds under
    /// <c>Relay:Attention:AnomalyThresholds:&lt;query&gt;</c> with <c>Ratio</c> (0 turns that check off), <c>MinSamples</c> and, for <c>quality_guardrails</c> closures, <c>DropRatio</c>.
    /// </summary>
    private static AnomalyOptions AnomalySettings(IConfiguration configuration)
    {
        var thresholds = new Dictionary<string, AnomalyThreshold>(StringComparer.Ordinal);
        foreach (var section in configuration.GetSection("Relay:Attention:AnomalyThresholds").GetChildren())
            thresholds[section.Key] = new(section.GetValue("Ratio", RelayLimits.DefaultAnomalyRatio), section.GetValue("MinSamples", RelayLimits.DefaultAnomalyMinSamples), section.GetValue("DropRatio", RelayLimits.DefaultAnomalyDropRatio));
        return new AnomalyOptions
        {
            Interval = configuration.GetValue("Relay:Attention:AnomalyInterval", TimeSpan.FromMinutes(RelayLimits.DefaultAnomalyIntervalMinutes)),
            Thresholds = thresholds
        }.Validated();
    }

    private static string DefaultDatabasePath(IHostEnvironment environment) => Path.Combine(environment.ContentRootPath, "data", "relay.db");
}
