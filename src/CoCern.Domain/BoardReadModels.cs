using System.Text.Json;
using CoCern.Contracts;
using CoCern.Contracts.Dashboard;
using CoCern.Domain.State;

namespace CoCern.Domain;

/// <summary>
/// The dashboard's standing panel for a stored board: its standing preferences (<see cref="StandingPreferences"/>) and its standing
/// orders (<see cref="StandingOrders"/>). It reads the keys those write, as the board's state declares them (<see cref="StandingState"/>,
/// <see cref="OrdersState"/>), so storage extracts only the two sub-documents the board keeps them in and asks here, instead of knowing
/// their layout. Every text it returns is <see cref="DashboardText.Safe"/>.
/// </summary>
public static class BoardReadModels
{
    /// <summary>The board's top-level key for its standing preferences: lines, grants, settings, proposals and history.</summary>
    public const string StandingKey = BoardState.StandingKey;

    /// <summary>The board's top-level key for its standing orders (<c>items</c>) and their <c>log</c>.</summary>
    public const string OrdersKey = BoardState.OrdersKey;

    private static readonly JsonDocumentOptions Options = new() { MaxDepth = 64 };

    /// <summary>
    /// One board's panel, from its grain key and its stored <see cref="StandingKey"/> and <see cref="OrdersKey"/> sub-documents (JSON text,
    /// null when the board has none): lines, grants and pending proposals with their provenance, whether each proposal would replace a
    /// line or grant, the board's settings and change history; its orders and order log. History and log are newest first, at most
    /// <see cref="RelayLimits.StandingHistoryEntries"/> and <see cref="RelayLimits.OrderLogEntries"/> entries. A value of the wrong type
    /// reads as empty: a line, grant, proposal, order or action that is not an object is left out, and a history or log entry that is not
    /// one shows as an empty entry.
    /// </summary>
    /// <exception cref="JsonException">A sub-document is not JSON, or nests deeper than 64 levels.</exception>
    public static DashboardStanding Standing(string boardKey, string? standing, string? orders)
    {
        var board = ProjectStanding(boardKey, standing ?? "{}");
        return board with { Orders = ProjectOrders(orders ?? "{}", out var log), OrderLog = log };
    }

    private static DashboardStanding ProjectStanding(string key, string json)
    {
        var (repositoryId, boardId) = BoardAddress(key);
        using var document = JsonDocument.Parse(json, Options);
        var standing = document.RootElement;
        var lines = Values(Property(standing, StandingState.LinesKey)).Select(line => new DashboardStandingLine
        {
            Id = Text(line, StandingLine.IdKey), Text = Text(line, StandingLine.TextKey), Author = Text(line, StandingLine.AuthorKey), ApprovedBy = Text(line, StandingLine.ApprovedByKey),
            ApprovedAt = Date(line, StandingLine.ApprovedAtKey), Decision = DecisionEvidence(line)
        }).ToArray();
        var grants = Values(Property(standing, StandingState.GrantsKey)).Select(grant => new DashboardStandingGrant
        {
            Id = Text(grant, StandingGrant.IdKey), Scope = Text(grant, StandingGrant.ScopeKey), Targets = Strings(Property(grant, StandingGrant.TargetsKey)),
            ExpiresAt = Date(grant, StandingGrant.ExpiresAtKey), Grantor = Text(grant, StandingGrant.GrantorKey), ProposedBy = Text(grant, StandingGrant.ProposedByKey),
            GrantedAt = Date(grant, StandingGrant.GrantedAtKey), Decision = DecisionEvidence(grant)
        }).ToArray();
        var proposals = Values(Property(standing, StandingState.ProposalsKey)).Select(proposal =>
        {
            var line = Property(proposal, StandingProposal.LineKey);
            var grant = Property(proposal, StandingProposal.GrantKey);
            var item = line.ValueKind == JsonValueKind.Object ? line : grant;
            // Approving a proposal writes the line or grant under its own ID, or under the proposal's when it names none.
            var id = Text(item, StandingLine.IdKey) is { Length: > 0 } named ? named : Text(proposal, StandingProposal.IdKey);
            return new DashboardStandingProposal
            {
                Id = Text(proposal, StandingProposal.IdKey), Kind = line.ValueKind == JsonValueKind.Object ? "line" : "grant",
                Summary = line.ValueKind == JsonValueKind.Object
                    ? Text(line, StandingLine.TextKey)
                    : $"{Text(grant, StandingGrant.ScopeKey)}: {string.Join(", ", Strings(Property(grant, StandingGrant.TargetsKey)))} until {Text(grant, StandingGrant.ExpiresAtKey)}",
                ProposedBy = Text(proposal, StandingProposal.ProposedByKey), ProposedAt = Date(proposal, StandingProposal.ProposedAtKey),
                Replaces = line.ValueKind == JsonValueKind.Object ? lines.Any(x => x.Id == id) : grants.Any(x => x.Id == id),
                SessionId = Text(proposal, StandingProposal.SessionIdKey), ExpiresAt = line.ValueKind == JsonValueKind.Object ? null : Date(grant, StandingGrant.ExpiresAtKey),
                PresentedBy = Text(proposal, StandingProposal.PresentedByKey), PresentedAt = Date(proposal, StandingProposal.PresentedAtKey)
            };
        }).ToArray();
        var history = Property(standing, StandingState.HistoryKey) is { ValueKind: JsonValueKind.Array } changes
            ? changes.EnumerateArray().Reverse().Take(RelayLimits.StandingHistoryEntries).Select(change => new DashboardStandingChange
            {
                Revision = Long(Property(change, StandingChange.RevisionKey)), By = Text(change, StandingChange.ByKey), At = Date(change, StandingChange.AtKey),
                Changes = Strings(Property(change, StandingChange.ChangesKey)), Decision = DecisionEvidence(change)
            }).ToArray()
            : [];
        return new()
        {
            RepositoryId = repositoryId, BoardId = boardId,
            Board = DashboardText.Safe(Uri.UnescapeDataString(key)), Revision = Long(Property(standing, StandingState.RevisionKey)),
            RequireResources = Property(Property(standing, StandingState.SettingsKey), StandingSettings.RequireResourcesKey).ValueKind == JsonValueKind.True,
            Lines = lines, Grants = grants, Proposals = proposals, History = history
        };
    }

    /// <summary>The repository and board a board's grain key names; both empty for an invalid key.</summary>
    internal static (string RepositoryId, string BoardId) BoardAddress(string key)
    {
        // Split the encoded key before decoding: a slash inside either ID is data, not a separator.
        var address = key.Split('/');
        var repositoryId = address.Length == 2 ? Uri.UnescapeDataString(address[0]) : "";
        var boardId = address.Length == 2 ? Uri.UnescapeDataString(address[1]) : "";
        return ManagementId(repositoryId) && ManagementId(boardId) && AggregateKinds.BoardKey(repositoryId, boardId) == key ? (repositoryId, boardId) : ("", "");
    }

    private static bool ManagementId(string id) => !string.IsNullOrWhiteSpace(id)
        && id.Length <= RelayLimits.MaxIdentifierLength && !id.Any(char.IsControl);

    /// <summary>A board's orders (trigger, actions, approval, expiry, runs, failures) and its order log, newest first.</summary>
    private static DashboardOrder[] ProjectOrders(string json, out DashboardOrderLogEntry[] log)
    {
        using var document = JsonDocument.Parse(json, Options);
        var orders = document.RootElement;
        log = Property(orders, OrdersState.LogKey) is { ValueKind: JsonValueKind.Array } entries
            ? entries.EnumerateArray().Reverse().Take(RelayLimits.OrderLogEntries)
                .Select(entry => new DashboardOrderLogEntry { At = Date(entry, OrderLogEntry.AtKey), OrderId = Text(entry, OrderLogEntry.OrderIdKey), What = Text(entry, OrderLogEntry.WhatKey) }).ToArray()
            : [];
        return Values(Property(orders, OrdersState.ItemsKey)).Select(order =>
        {
            var trigger = Property(order, OrderRecord.TriggerKey);
            var on = Text(trigger, OrderTrigger.OnKey);
            var actions = Property(order, OrderRecord.ActionsKey) is { ValueKind: JsonValueKind.Array } list
                ? list.EnumerateArray().Where(action => action.ValueKind == JsonValueKind.Object)
                    .Select(action => string.Join(" ", new[] { Text(action, OrderStep.DoKey), Text(action, OrderStep.TaskIdKey), Text(action, OrderStep.SessionIdKey) is { Length: > 0 } session ? "to " + session : "" }.Where(part => part.Length > 0))).ToArray()
                : [];
            return new DashboardOrder
            {
                Id = Text(order, OrderRecord.IdKey), Trigger = on, Target = Text(trigger, OrderTrigger.TargetKey(on)), Actions = actions,
                ApprovedBy = Text(order, OrderRecord.ApprovedByKey), ExpiresAt = Date(order, OrderRecord.ExpiresAtKey), Runs = Long(Property(order, OrderRecord.RunsKey)),
                Failures = Long(Property(order, OrderRecord.FailuresKey)), Disabled = Property(order, OrderRecord.DisabledKey).ValueKind == JsonValueKind.True,
                LastError = Text(order, OrderRecord.LastErrorKey)
            };
        }).ToArray();
    }

    private static DashboardDecisionEvidence? DecisionEvidence(JsonElement record)
    {
        var decision = Property(record, StandingDecision.Key);
        if (decision.ValueKind != JsonValueKind.Object) return null;
        // Unlike labels, these intentionally stored words must survive exactly. Razor encodes them as plain text.
        static string Exact(JsonElement value, string name) => Property(value, name) is { ValueKind: JsonValueKind.String } item ? item.GetString()! : "";
        static string Target(JsonElement effect) => Property(effect, "line").ValueKind == JsonValueKind.Object
            ? Exact(Property(effect, "line"), StandingLine.TextKey)
            : Exact(Property(effect, "grant"), StandingGrant.IdKey);
        var effect = Property(decision, StandingDecision.EffectKey);
        var grant = Property(effect, "grant");
        return new()
        {
            AuditRef = Exact(decision, StandingDecision.AuditRefKey), PrincipalId = Exact(decision, StandingDecision.PrincipalIdKey),
            SessionId = Exact(decision, StandingDecision.SessionIdKey), At = Date(decision, StandingDecision.AtKey),
            HumanWords = Exact(decision, StandingDecision.HumanWordsKey), Outcome = Exact(decision, StandingDecision.OutcomeKey),
            ProposalId = Exact(decision, StandingDecision.ProposalIdKey), Target = Target(effect), Replaces = Target(Property(effect, "replaces")),
            Scope = Exact(grant, StandingGrant.ScopeKey), Targets = Property(grant, StandingGrant.TargetsKey) is { ValueKind: JsonValueKind.Array } targets
                ? targets.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String).Select(item => item.GetString()!).ToArray() : [],
            ExpiresAt = Date(grant, StandingGrant.ExpiresAtKey)
        };
    }

    private static JsonElement Property(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var result) ? result : default;

    private static string Text(JsonElement value, string name) { var item = Property(value, name); return item.ValueKind == JsonValueKind.String ? DashboardText.Safe(item.GetString()!) : ""; }

    private static DateTimeOffset? Date(JsonElement value, string name) { var item = Property(value, name); return item.ValueKind == JsonValueKind.String && item.TryGetDateTimeOffset(out var date) ? date : null; }

    private static long Long(JsonElement value) => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) ? number : 0;

    /// <summary>The object values of a JSON object (a map keyed by ID); anything else holds none.</summary>
    private static IEnumerable<JsonElement> Values(JsonElement value) =>
        value.ValueKind == JsonValueKind.Object ? value.EnumerateObject().Select(property => property.Value).Where(item => item.ValueKind == JsonValueKind.Object).ToArray() : [];

    private static string[] Strings(JsonElement value) =>
        value.ValueKind == JsonValueKind.Array ? value.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String).Select(item => DashboardText.Safe(item.GetString()!)).ToArray() : [];
}
