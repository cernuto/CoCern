using System.Text.Json;
using CoCern.Adapter;
using CoCern.Contracts;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using Xunit;

namespace CoCern.Tests;

public sealed class McpReadResultsTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Structured_reads_preserve_committed_payloads_and_reference_large_text_without_hiding_delivery(bool http)
    {
        var small = Read(JsonSerializer.SerializeToElement(new { items = Array.Empty<object>(), total = 0 }));
        Assert.Equal("structured", small.StructuredContent!.Value.GetProperty("representation").GetString());
        Assert.Equal(0, small.StructuredContent.Value.GetProperty("data").GetProperty("total").GetInt32());
        Assert.False(small.StructuredContent.Value.TryGetProperty("dataInText", out _));
        Assert.True(JsonSerializer.SerializeToUtf8Bytes(small, McpJsonUtilities.DefaultOptions).Length <= RelayLimits.MaxPayloadBytes);

        // A canonical final piece plus offer has already committed its delivery effects before MCP presentation.
        var canonical = "{\"messageId\":\"mail-1\",\"text\":\"" + string.Concat(Enumerable.Repeat("😀漢字", 1000)) + "\"}";
        var committed = JsonSerializer.SerializeToElement(new
        {
            items = new[] { new { messageId = "mail-1", part = canonical, partOffset = 0, nextPartOffset = (int?)null,
                truncated = true, chars = canonical.Length } },
            nextCursor = 19, truncated = true, total = 20,
            attention = new { scope = "offer", offer = new { id = "attention-1", taskId = "task-1" } }
        });
        var large = Read(committed);
        Assert.NotEqual(true, large.IsError);
        var structured = large.StructuredContent!.Value;
        Assert.True(structured.GetProperty("ok").GetBoolean());
        Assert.Equal("text", structured.GetProperty("representation").GetString());
        Assert.Equal(JsonValueKind.Null, structured.GetProperty("data").ValueKind);
        var reference = structured.GetProperty("dataInText");
        Assert.Equal(0, reference.GetProperty("contentIndex").GetInt32());
        Assert.Equal(http ? "dataJson" : "data", reference.GetProperty("property").GetString());
        Assert.Equal(http ? "json" : "object", reference.GetProperty("encoding").GetString());
        using var envelope = JsonDocument.Parse(Assert.IsType<TextContentBlock>(large.Content[reference.GetProperty("contentIndex").GetInt32()]).Text);
        var referenced = envelope.RootElement.GetProperty(reference.GetProperty("property").GetString()!);
        using var recovered = JsonDocument.Parse(reference.GetProperty("encoding").GetString() == "json" ? referenced.GetString()! : referenced.GetRawText());
        var page = recovered.RootElement;
        Assert.Equal(19, page.GetProperty("nextCursor").GetInt32());
        Assert.Equal(20, page.GetProperty("total").GetInt32());
        var item = page.GetProperty("items")[0];
        Assert.Equal("mail-1", item.GetProperty("messageId").GetString());
        Assert.Equal(canonical, item.GetProperty("part").GetString());
        Assert.Equal(canonical.Length, item.GetProperty("chars").GetInt32());
        Assert.Equal(0, item.GetProperty("partOffset").GetInt32());
        Assert.Equal(JsonValueKind.Null, item.GetProperty("nextPartOffset").ValueKind);
        Assert.Equal("attention-1", page.GetProperty("attention").GetProperty("offer").GetProperty("id").GetString());
        Assert.Equal("task-1", page.GetProperty("attention").GetProperty("offer").GetProperty("taskId").GetString());
        Assert.True(JsonSerializer.SerializeToUtf8Bytes(structured).Length < 512);

        CallToolResult Read(JsonElement data)
        {
            var reply = new AdapterReply(true, "Ok", data, 17);
            var legacy = http ? JsonSerializer.Serialize(new { ok = true, code = "Ok", dataJson = data.GetRawText(), version = 17 }, AdapterReply.JsonOptions)
                : ModelReply.Text(RelayTools.CheckInbox, reply);
            var result = http ? McpReadResults.FromHost(RelayTools.CheckInbox, true, "Ok", data.GetRawText(), 17)
                : McpReadResults.FromAdapter(RelayTools.CheckInbox, reply);
            Assert.Equal(legacy, Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text);
            return result;
        }
    }
}
