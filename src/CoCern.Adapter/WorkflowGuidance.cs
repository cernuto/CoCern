using System.Text;

namespace CoCern.Adapter;

/// <summary>The packaged workflow document, shared by skills and explicitly selected MCP context.</summary>
public static class WorkflowGuidance
{
    private static readonly Lazy<string> document = new(ReadDocument);
    private static readonly Lazy<string> text = new(ReadText);

    public static string SkillDocument => document.Value;
    public static string Text => text.Value;

    private static string ReadDocument()
    {
        using var stream = typeof(WorkflowGuidance).Assembly.GetManifestResourceStream("CoCern.Workflow.SKILL.md")
            ?? throw new IOException("The CoCern workflow skill is unavailable.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static string ReadText()
    {
        var source = SkillDocument;
        if (!source.StartsWith("---\n", StringComparison.Ordinal) && !source.StartsWith("---\r\n", StringComparison.Ordinal)) return source;
        var closing = source.IndexOf("\n---\n", 4, StringComparison.Ordinal);
        if (closing >= 0) return source[(closing + 5)..].TrimStart('\r', '\n');
        closing = source.IndexOf("\n---\r\n", 4, StringComparison.Ordinal);
        if (closing >= 0) return source[(closing + 6)..].TrimStart('\r', '\n');
        throw new IOException("The CoCern workflow skill has an invalid metadata header.");
    }
}
