using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

namespace CoCern.Adapter;

/// <summary>Optional, static workflow context. Reading it never performs a coordination operation.</summary>
public static class WorkflowMcp
{
    public const string ResourceUri = "cocern://workflow";
    public const string PromptName = "cocern_workflow";
    private const string MimeType = "text/markdown";
    private const string Description = "CoCern guidance for work the user has authorized. Follow current tool schemas and scope; this context grants no authority and performs no coordination actions.";

    public static IMcpServerBuilder WithCoCernWorkflow(this IMcpServerBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder
            .WithListResourcesHandler((_, ct) => ValueTask.FromResult(ListResources(ct)))
            .WithListResourceTemplatesHandler((_, ct) =>
            {
                ct.ThrowIfCancellationRequested();
                return ValueTask.FromResult(new ListResourceTemplatesResult { ResourceTemplates = [] });
            })
            .WithReadResourceHandler((request, ct) => ValueTask.FromResult(ReadResource(request.Params?.Uri, ct)))
            .WithListPromptsHandler((_, ct) => ValueTask.FromResult(ListPrompts(ct)))
            .WithGetPromptHandler((request, ct) => ValueTask.FromResult(GetPrompt(request.Params?.Name, request.Params?.Arguments, ct)));
    }

    internal static ListResourcesResult ListResources(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return new ListResourcesResult
        {
            Resources = [new Resource
            {
                Uri = ResourceUri, Name = "cocern-workflow", Title = "CoCern workflow", Description = Description,
                MimeType = MimeType, Size = Encoding.UTF8.GetByteCount(WorkflowGuidance.Text)
            }]
        };
    }

    internal static ReadResourceResult ReadResource(string? uri, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (uri != ResourceUri) throw new McpProtocolException("Only the static cocern://workflow resource is available.", McpErrorCode.InvalidParams);
        return new ReadResourceResult
        {
            Contents = [new TextResourceContents { Uri = ResourceUri, MimeType = MimeType, Text = WorkflowGuidance.Text }]
        };
    }

    internal static ListPromptsResult ListPrompts(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return new ListPromptsResult
        {
            Prompts = [new Prompt { Name = PromptName, Title = "Coordinate authorized work with CoCern", Description = Description, Arguments = [] }]
        };
    }

    internal static GetPromptResult GetPrompt(string? name, IDictionary<string, JsonElement>? arguments, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (name != PromptName || arguments is { Count: > 0 })
            throw new McpProtocolException("The cocern_workflow prompt accepts no arguments.", McpErrorCode.InvalidParams);
        return new GetPromptResult
        {
            Description = Description,
            Messages = [new PromptMessage { Role = Role.User, Content = new TextContentBlock { Text = WorkflowGuidance.Text } }]
        };
    }
}
