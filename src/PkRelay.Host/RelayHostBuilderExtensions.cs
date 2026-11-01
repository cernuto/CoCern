using System.Net;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Orleans.Configuration;
using Orleans.Hosting;
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
        // The search port is passed only when the store keeps an index; otherwise the search tool answers SearchUnavailable.
        builder.Services.Replace(ServiceDescriptor.Singleton(services => new RelayApplication(
            services.GetRequiredService<Orleans.IGrainFactory>(), services.GetService<IMachineDirectory>(), services.GetService<IActivitySink>(),
            services.GetService<SqliteStore>() is { SearchEnabled: true } store ? store : null, services.GetRequiredService<AttentionOptions>())));
        builder.AddRelayOrleans();
        builder.Services.AddRelayDashboard();
        builder.Services.AddHostedService<DueWorkDispatcher>();
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

    private static string DefaultDatabasePath(IHostEnvironment environment) => Path.Combine(environment.ContentRootPath, "data", "relay.db");
}
