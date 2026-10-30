using PkRelay.Host;

// Composition root only: each concern is registered and mapped by its own extension.
public partial class Program
{
    public static async Task Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.AddRelayHost();
        var app = builder.Build();
        app.UseRelayPipeline();
        app.MapRelayEndpoints();
        await app.RunAsync();
    }
}
