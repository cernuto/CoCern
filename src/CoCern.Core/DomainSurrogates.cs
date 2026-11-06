using Orleans;
using CoCern.Domain;

namespace CoCern.Core;

// Orleans serializes grain-call arguments through these surrogates, so the domain records carry no Orleans attributes.

[GenerateSerializer]
public struct RelayCallerSurrogate
{
    [Id(0)] public string PrincipalId;
    [Id(1)] public bool IsAdministrator;
    [Id(2)] public string[] Repositories;
    [Id(3)] public bool IsInternal;
    [Id(4)] public string? AuthenticatedMachineId;
    [Id(5)] public string[]? ActingParticipant;
    [Id(6)] public string[][]? GoalParticipants;
}

[RegisterConverter]
public sealed class RelayCallerSurrogateConverter : IConverter<RelayCaller, RelayCallerSurrogate>
{
    public RelayCaller ConvertFromSurrogate(in RelayCallerSurrogate surrogate) => new(surrogate.PrincipalId, surrogate.IsAdministrator, surrogate.Repositories, surrogate.IsInternal, surrogate.AuthenticatedMachineId)
    {
        ActingParticipant = surrogate.IsInternal ? Participant(surrogate.ActingParticipant) : null,
        GoalParticipants = surrogate.IsInternal && surrogate.GoalParticipants is { Length: <= 32 } members
            ? members.All(member => Participant(member) is not null) ? members.Select(member => Participant(member)!).ToArray() : null : null
    };
    public RelayCallerSurrogate ConvertToSurrogate(in RelayCaller value) => new()
    {
        PrincipalId = value.PrincipalId, IsAdministrator = value.IsAdministrator, Repositories = value.Repositories, IsInternal = value.IsInternal, AuthenticatedMachineId = value.AuthenticatedMachineId,
        ActingParticipant = value.IsInternal ? Fields(value.ActingParticipant) : null,
        GoalParticipants = value.IsInternal ? value.GoalParticipants?.Select(member => Fields(member)!).ToArray() : null
    };

    private static string[]? Fields(VerifiedNativeParticipant? participant) => participant is null ? null : [participant.ParticipantId, participant.SessionId, participant.RepositoryId];
    private static VerifiedNativeParticipant? Participant(string[]? fields) => fields is { Length: 3 } && fields.All(field => !string.IsNullOrWhiteSpace(field)) ? new(fields[0], fields[1], fields[2]) : null;
}

[GenerateSerializer]
public struct RelayReplySurrogate
{
    [Id(0)] public bool Ok;
    [Id(1)] public string Code;
    [Id(2)] public string DataJson;
    [Id(3)] public long Version;
}

[RegisterConverter]
public sealed class RelayReplySurrogateConverter : IConverter<RelayReply, RelayReplySurrogate>
{
    public RelayReply ConvertFromSurrogate(in RelayReplySurrogate surrogate) => new(surrogate.Ok, surrogate.Code, surrogate.DataJson, surrogate.Version);
    public RelayReplySurrogate ConvertToSurrogate(in RelayReply value) => new() { Ok = value.Ok, Code = value.Code, DataJson = value.DataJson, Version = value.Version };
}
