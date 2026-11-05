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
}

[RegisterConverter]
public sealed class RelayCallerSurrogateConverter : IConverter<RelayCaller, RelayCallerSurrogate>
{
    public RelayCaller ConvertFromSurrogate(in RelayCallerSurrogate surrogate) => new(surrogate.PrincipalId, surrogate.IsAdministrator, surrogate.Repositories, surrogate.IsInternal, surrogate.AuthenticatedMachineId);
    public RelayCallerSurrogate ConvertToSurrogate(in RelayCaller value) => new() { PrincipalId = value.PrincipalId, IsAdministrator = value.IsAdministrator, Repositories = value.Repositories, IsInternal = value.IsInternal, AuthenticatedMachineId = value.AuthenticatedMachineId };
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
