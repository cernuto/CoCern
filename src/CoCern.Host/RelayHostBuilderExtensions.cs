using System.Net;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Orleans.Configuration;
using Orleans.Hosting;
using CoCern.Analytics;
using CoCern.Contracts;
using CoCern.Core;
using CoCern.Domain;
using CoCern.Storage;
using Serilog;
using Serilog.Events;

namespace CoCern.Host;

using Dashboard;

public static class RelayHostBuilderExtensions
{
    private const int DefaultSiloPort = 21111;
    private const int DefaultGatewayPort = 31111;
    private const string ServiceId = "CoCern";

    public static WebApplicationBuilder AddRelayHost(this WebApplicationBuilder builder)
    {
        if (string.IsNullOrWhiteSpace(builder.Configuration["urls"])) builder.WebHost.UseUrls(HubNetwork.DefaultUrl);
        builder.WebHost.ConfigureKestrel((context, options) =>
        {
            options.Limits.MaxRequestBodySize = RelayLimits.MaxPayloadBytes;
            HubNetwork.RefusePlainNetworkHttp(context.Configuration);
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
        builder.Services.AddCoCernCore();
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
        // Finished tasks leave their board after Relay:Board:ArchiveAfterDays (default 30), or sooner when it nears its task limit.
        builder.Services.AddSingleton(services => new BoardOptions
        {
            ArchiveAfterDays = services.GetRequiredService<IConfiguration>().GetValue("Relay:Board:ArchiveAfterDays", RelayLimits.DefaultArchiveAfterDays)
        }.Validated());
        // Standing orders are on by default; the board grains read the same options to decide whether orders run.
        builder.Services.AddSingleton(services =>
        {
            var configuration = services.GetRequiredService<IConfiguration>();
            return new OrderOptions
            {
                Enabled = configuration.GetValue("Relay:Orders:Enabled", true),
                DefaultExpiryDays = configuration.GetValue("Relay:Orders:DefaultExpiryDays", RelayLimits.DefaultOrderExpiryDays)
            }.Validated();
        });
        builder.Services.AddSingleton(services => TokenSettings(services.GetRequiredService<IConfiguration>()));
        builder.Services.AddSingleton(services => FeedbackSettings(services.GetRequiredService<IConfiguration>()));
        // A port that is off is passed as null, and its tools answer *Unavailable (search, analytics, anomaly, doc cache, feedback).
        builder.Services.Replace(ServiceDescriptor.Singleton(services =>
        {
            var queries = services.GetService<IAnalyticsQueries>();
            var counter = services.GetRequiredService<ITokenCounter>();
            return Warmed(new RelayApplication(
                services.GetRequiredService<Orleans.IGrainFactory>(),
                machines: services.GetService<IMachineDirectory>(),
                activity: services.GetService<IActivitySink>(),
                search: services.GetService<IFullTextSearch>() is { Enabled: true } search ? search : null,
                attention: services.GetRequiredService<AttentionOptions>(),
                analytics: queries is null ? null : new AnalyticsTool(queries, services.GetRequiredService<IConfiguration>().GetValue("Relay:Analytics:AllowAdHocSql", true), services.GetRequiredService<ILoggerFactory>().CreateLogger<AnalyticsTool>()),
                anomaly: queries is null ? null : services.GetRequiredService<AnomalyOptions>(),
                orders: services.GetRequiredService<OrderOptions>(),
                docs: services.GetService<DocCacheTool>(),
                tokens: counter,
                tokenOptions: services.GetRequiredService<TokenOptions>(),
                feedback: services.GetRequiredService<FeedbackOptions>() is { Enabled: true } feedback ? new FeedbackRules(feedback, counter, services.GetService<IFeedbackRuleStore>()) : null,
                archive: services.GetService<ITaskArchive>()));
        }));
        // One shared tokenizer for every token count; it loads its vocabulary on first use.
        builder.Services.AddSingleton<ITokenCounter, O200kTokenCounter>();
        builder.Services.AddDocCache();
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
            .AddCoCernSqlite(DefaultDatabasePath(builder.Environment)));
        // Resolve final configuration through DI so application-factory and hosting overrides apply before any store/listener opens.
        builder.Services.Replace(ServiceDescriptor.Singleton<SqliteStore>(services =>
        {
            var configuration = services.GetRequiredService<IConfiguration>();
            var activity = new ActivityOptions
            {
                Enabled = configuration.GetValue("Relay:Activity:Enabled", true),
                RetentionDays = configuration.GetValue("Relay:Activity:RetentionDays", 30) is var days and >= 1 ? days : throw new InvalidOperationException("Relay:Activity:RetentionDays must be at least 1.")
            };
            var search = new SearchOptions { Enabled = configuration.GetValue("Relay:Search:Enabled", true) };
            return new SqliteStore(DatabasePath(configuration, services.GetRequiredService<IHostEnvironment>()), activity, search)
            {
                // The activity writer counts each call's request and reply tokens, so counting never slows a call.
                TokenCounter = services.GetRequiredService<TokenOptions>().Enabled ? services.GetRequiredService<ITokenCounter>() : null,
                Logger = services.GetService<ILoggerFactory>()?.CreateLogger<SqliteStore>()
            };
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
    /// Snapshots and DuckDB analytics, on by default. The analytics service is created only when enabled, so DuckDB's native library
    /// is not loaded otherwise; <see cref="IAnalyticsQueries"/> resolves to null when analytics is off. The extension comes bundled next
    /// to the binaries; without a usable one the snapshot service warns once and queries answer AnalyticsUnavailable.
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
        // Resolved lazily, so settings applied after registration (hosts, tests) still decide; null when off.
        services.AddSingleton<IAnalyticsQueries>(provider => provider.GetRequiredService<AnalyticsSettings>().Enabled ? provider.GetRequiredService<AnalyticsService>() : null!);
        services.AddHostedService<AnalyticsSnapshotService>();
    }

    /// <summary>
    /// The doc cache, on by default. Like analytics, its parts resolve only when <c>Relay:DocCache:Enabled</c> is on: otherwise the
    /// tool and the document source resolve to null, the sync service returns at once without building its runner, git never runs,
    /// and the document grains keep the default idle time. On with no repositories configured, git never runs either, and the tools
    /// answer NoRepositoriesConfigured.
    /// </summary>
    private static void AddDocCache(this IServiceCollection services)
    {
        services.AddSingleton<DocCacheActivity>();
        services.AddSingleton(provider => DocCacheSettings.From(provider.GetRequiredService<IConfiguration>(), provider.GetRequiredService<IHostEnvironment>()));
        services.TryAddSingleton<GitCli>();
        services.AddSingleton<IDocSource>(provider => provider.GetRequiredService<DocCacheSettings>().Enabled ? new GitDocSource(provider.GetRequiredService<DocCacheSettings>(), provider.GetRequiredService<GitCli>()) : null!);
        services.AddSingleton<DocSyncRunner>();
        services.AddSingleton<IDocSync>(provider => provider.GetRequiredService<DocSyncRunner>());
        services.AddSingleton(provider => provider.GetRequiredService<DocCacheSettings>() is { Enabled: true } settings
            ? new DocCacheTool(provider.GetRequiredService<Orleans.IGrainFactory>(), provider.GetRequiredService<ITokenCounter>(), provider.GetRequiredService<IDocSync>(), settings.Repositories.Select(repository => repository.Id).ToArray())
            : null!);
        services.AddHostedService<SpecSyncService>();
        // Idle deactivation is the cache's eviction: an unused document grain drops its index after Relay:DocCache:IdleMinutes.
        services.AddOptions<GrainCollectionOptions>().Configure<IConfiguration>((options, configuration) =>
        {
            if (configuration.GetValue("Relay:DocCache:Enabled", true))
                options.ClassSpecificCollectionAge[typeof(DocumentGrain).FullName!] = TimeSpan.FromMinutes(configuration.GetValue("Relay:DocCache:IdleMinutes", RelayLimits.DefaultDocIdleMinutes));
        });
    }

    /// <summary>Counts the tool definitions (and loads the tokenizer) in the background at startup, so no call waits for it.</summary>
    private static RelayApplication Warmed(RelayApplication application)
    {
        _ = Task.Run(application.WarmTokenCounts);
        return application;
    }

    /// <summary>
    /// <c>Relay:Tokens:Enabled</c> (default true), <c>Relay:Tokens:Enforce</c> (default true: every tool but the doc cache's is compacted
    /// to its budget; false only measures) and <c>Relay:Tokens:EnforceTools</c> (when listed, only those tools are enforced).
    /// </summary>
    private static TokenOptions TokenSettings(IConfiguration configuration)
    {
        var listed = configuration.GetSection("Relay:Tokens:EnforceTools").GetChildren().Select(tool => tool.Value ?? "").Where(tool => tool.Length > 0).ToHashSet(StringComparer.Ordinal);
        return new TokenOptions
        {
            Enabled = configuration.GetValue("Relay:Tokens:Enabled", true),
            EnforceTools = !configuration.GetValue("Relay:Tokens:Enforce", true) ? new HashSet<string>(StringComparer.Ordinal)
                : listed.Count > 0 ? listed : TokenOptions.AllEnforceable
        }.Validated();
    }

    /// <summary><c>Relay:Feedback:Tips</c>, <c>ProposeRules</c> (both default true) and <c>MinFollowRate</c> (default 0.2).</summary>
    private static FeedbackOptions FeedbackSettings(IConfiguration configuration) => new FeedbackOptions
    {
        Tips = configuration.GetValue("Relay:Feedback:Tips", true),
        ProposeRules = configuration.GetValue("Relay:Feedback:ProposeRules", true),
        MinFollowRate = configuration.GetValue("Relay:Feedback:MinFollowRate", RelayLimits.DefaultMinFollowRate)
    }.Validated();

    /// <summary>
    /// <c>Relay:Attention:AnomalyInterval</c> (default 1 hour; 0 turns the monitor off) and per-query thresholds under
    /// <c>Relay:Attention:AnomalyThresholds:&lt;query&gt;</c> with <c>Ratio</c> (0 turns that check off), <c>MinSamples</c> and, for <c>quality_guardrails</c> closures, <c>DropRatio</c>.
    /// </summary>
    private static AnomalyOptions AnomalySettings(IConfiguration configuration)
    {
        var thresholds = new Dictionary<string, AnomalyThreshold>(StringComparer.Ordinal);
        foreach (var section in configuration.GetSection("Relay:Attention:AnomalyThresholds").GetChildren())
        {
            // An alert query keeps its own defaults (and a compaction rate its fixed limit) for whatever the section leaves out.
            var defaults = AnomalyOptions.AlertQueries.TryGetValue(section.Key, out var alert) ? alert : new AnomalyThreshold(RelayLimits.DefaultAnomalyRatio, RelayLimits.DefaultAnomalyMinSamples);
            thresholds[section.Key] = new(section.GetValue("Ratio", defaults.Ratio), section.GetValue("MinSamples", defaults.MinSamples), section.GetValue("DropRatio", defaults.DropRatio), defaults.Limit);
        }
        return new AnomalyOptions
        {
            Interval = configuration.GetValue("Relay:Attention:AnomalyInterval", TimeSpan.FromMinutes(RelayLimits.DefaultAnomalyIntervalMinutes)),
            Thresholds = thresholds,
            // Manager alerts are extra anomaly checks; like every check they run only with analytics on.
            Alerts = configuration.GetValue("Relay:Feedback:Alerts", true)
        }.Validated();
    }

    /// <summary>A relative configured path resolves from the content root, so a launch profile can point at the repository's data folder.</summary>
    internal static string DatabasePath(IConfiguration configuration, IHostEnvironment environment) =>
        configuration["Relay:DatabasePath"] is { } configured ? Path.Combine(environment.ContentRootPath, configured) : DefaultDatabasePath(environment);

    private static string DefaultDatabasePath(IHostEnvironment environment) => Path.Combine(environment.ContentRootPath, "data", "relay.db");
}
