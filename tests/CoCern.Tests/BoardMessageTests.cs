using System.Text.Json.Nodes;
using CoCern.Contracts;
using CoCern.Core;
using CoCern.Domain;
using CoCern.Domain.Messages;
using Xunit;

namespace CoCern.Tests;

/// <summary>
/// The board's typed operations: the hub's direct requests to a board, and the order effects between boards and locks. Each reads back
/// exactly what it writes and names a missing or mistyped field; a board or lock given a malformed one answers
/// <see cref="RelayErrorCodes.MalformedEffect"/> and changes nothing; and every order effect is advice.
/// </summary>
public sealed class BoardMessageTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-03T09:00:00Z");
    private static readonly RelayCaller Hub = new("alice", false, ["repo"], true);

    private static string Request<T>(T request) where T : class, IInternalRequest<T>
    {
        Assert.True(T.Decode(request.ToJson()).TryGet(out var decoded, out var fields), string.Join(", ", fields));
        Assert.Equal(request.ToJson().ToJsonString(), decoded.ToJson().ToJsonString());
        return decoded.ToJson().ToJsonString();
    }

    private static T Effect<T>(T message) where T : class, IEffectMessage<T>
    {
        Assert.True(T.Decode(message.ToJson()).TryGet(out var decoded, out var fields), string.Join(", ", fields));
        return decoded;
    }

    private static IReadOnlyList<string> RequestMissing<T>(JsonObject payload) where T : class, IInternalRequest<T>
    {
        Assert.False(T.Decode(payload).TryGet(out _, out var fields));
        return fields;
    }

    private static IReadOnlyList<string> EffectMissing<T>(JsonObject payload) where T : class, IEffectMessage<T>
    {
        Assert.False(T.Decode(payload).TryGet(out _, out var fields));
        return fields;
    }

    private static JsonObject Without(JsonObject payload, params string[] fields)
    {
        foreach (var field in fields) payload.Remove(field);
        return payload;
    }

    [Theory]
    [InlineData("repositoryId")]
    [InlineData("sessionId")]
    [InlineData("proposalId")]
    [InlineData("presentationRequest")]
    [InlineData("expectedEffect")]
    public void A_standing_presentation_request_requires_its_identity_correlation_and_exact_effect(string missing)
    {
        var request = new StandingPresentationRequest("repo", null, "s1", "p1", "check_inbox:r1", """{"k":"standing_proposal","id":"p1","b":"default","line":{"text":"Confirm first"}}""");
        Assert.False(request.ToJson().ContainsKey("boardId"));
        Assert.Equal([missing], RequestMissing<StandingPresentationRequest>(Without(request.ToJson(), missing)));
        var wrongShape = request.ToJson();
        wrongShape["expectedEffect"] = new JsonArray("not an effect");
        Assert.Equal(["expectedEffect"], RequestMissing<StandingPresentationRequest>(wrongShape));
    }

    [Fact]
    public void A_malformed_standing_presentation_request_changes_nothing_and_stays_internal()
    {
        var board = new StateEnvelope<CoordinationDocument>();
        var args = new StandingPresentationRequest("repo", null, "s1", "p1", "check_inbox:r1", "{}").ToJson();
        args.Remove("expectedEffect");
        var before = board.Value.Json;
        var refused = CoordinationStateMachine.Apply(board, "board", "key", Hub, Operations.PresentStanding, args, Now);
        Assert.False(refused.Ok);
        Assert.Equal(RelayErrorCodes.MalformedEffect, refused.Code);
        Assert.Contains("expectedEffect", refused.DataJson);
        Assert.Equal(before, board.Value.Json);
        Assert.Empty(board.Effects);
        var external = CoordinationStateMachine.Apply(board, "board", "key", Hub with { IsInternal = false }, Operations.PresentStanding, args, Now);
        Assert.False(external.Ok);
        Assert.Equal(RelayErrorCodes.InternalOnly, external.Code);
        Assert.Equal(before, board.Value.Json);
    }

}
