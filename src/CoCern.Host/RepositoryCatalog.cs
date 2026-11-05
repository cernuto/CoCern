using CoCern.Contracts;

namespace CoCern.Host;

/// <summary>Canonical configured names, plus repositories discovered by the existing document registry. This grants no access.</summary>
internal sealed class RepositoryCatalog
{
    private readonly string[] configured;
    private readonly DocRepositoryRegistry documents;

    public RepositoryCatalog(IConfiguration configuration, DocRepositoryRegistry documents)
    {
        this.documents = documents;
        configured = ConfiguredNames(configuration);
    }

    internal static string[] ConfiguredNames(IConfiguration configuration)
    {
        static IEnumerable<string> Scopes(IConfigurationSection section) => section.GetChildren()
            .SelectMany(entry => entry.GetSection("Repositories").GetChildren()).Select(entry => entry.Value ?? "");
        var configured = RepositoryIds.Configured(Scopes(configuration.GetSection("Relay:Clients"))
            .Concat(Scopes(configuration.GetSection("Relay:OAuth:Principals")))
            .Concat(configuration.GetSection("Relay:Discovery:Repositories").GetChildren().Select(entry => entry.Value ?? ""))
            .Concat(configuration.GetSection("Relay:DocCache:Repositories").GetChildren().Select(entry => entry["Id"] ?? "")));
        // Discovery's absent-setting default is not a second configured spelling.
        if (!configuration.GetSection("Relay:Discovery:Repositories").Exists() && RepositoryIds.Match("cocern", configured) is null)
            configured = [.. configured, "cocern"];
        return configured;
    }

    public string? Find(string supplied) => RepositoryIds.Match(supplied, configured)
        ?? RepositoryIds.Match(supplied, documents.Ids);
}
