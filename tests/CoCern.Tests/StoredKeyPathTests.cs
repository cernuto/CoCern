using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using CoCern.Analytics;
using CoCern.Contracts;
using CoCern.Core;
using CoCern.Domain;
using CoCern.Storage;
using Orleans.Runtime;
using Xunit;

namespace CoCern.Tests;

/// <summary>
/// The analytics views (Views.sql) and the dashboard's standing read (<see cref="SqliteStore.StandingQuery"/>) read stored aggregate
/// state by JSON path, outside the Domain. Each path they name must resolve in a board the Domain built through its own operations and
/// storage wrote, so renaming a Domain key fails here instead of quietly emptying a view or a dashboard panel. DuckDB cannot load in every
/// test environment, so the paths are read from the SQL text and resolved here.
/// </summary>
public sealed class StoredKeyPathTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-03T00:00:00Z");
    private static readonly RelayCaller Writer = new("alice", false, ["repo"], true);
    private static readonly RelayCaller Administrator = new("bob", true, ["repo"], true);

    [Fact]
    public async Task Every_path_the_analytics_views_read_resolves_in_a_board_the_domain_built()
    {
        string sql;
        using (var stream = typeof(AnalyticsService).Assembly.GetManifestResourceStream("CoCern.Analytics.Views.sql")!)
        using (var reader = new StreamReader(stream))
            sql = reader.ReadToEnd();
        var (fixture, store) = await StoredBoard();
        using (store)
        {
            var paths = AssertPathsResolve(sql, fixture);
            // The envelope, the board, its tasks and their messages and reports: a parser that stopped seeing them would check nothing.
            Assert.True(paths >= 20, $"Only {paths} JSON paths were found in Views.sql.");
        }
    }

    [Fact]
    public async Task Every_path_the_dashboards_standing_read_extracts_resolves_in_a_board_the_domain_built()
    {
        var (fixture, store) = await StoredBoard();
        using (store)
        {
            Assert.Equal(3, AssertPathsResolve(SqliteStore.StandingQuery, fixture));
            // Read through that query, the panel shows what the Domain wrote.
            var board = Assert.Single(Assert.IsType<DashboardStandingObservation>(await store.ReadStandingAsync()).Boards);
            Assert.NotEmpty(board.Lines);
            Assert.NotEmpty(board.Grants);
            Assert.NotEmpty(board.Proposals);
            Assert.NotEmpty(board.History);
            Assert.NotEmpty(board.Orders);
            Assert.NotEmpty(board.OrderLog);
        }
    }

    /// <summary>The stored envelope (<c>grain_state.payload</c>) and the board document inside it (<c>$.Value.Json</c>).</summary>
    private sealed record Fixture(JsonNode Envelope, JsonObject Board);

    /// <summary>
    /// One board holding every kind of entry the readers name: a planned task whose packet declares resources; a task claimed and worked,
    /// whose action an administrator's recovery abandoned and its writer then completed after all, with a message; standing lines, grants, a
    /// proposal and their history; and a standing order with its log. Storage writes it as the hub does.
    /// </summary>
    private static async Task<(Fixture, SqliteStore)> StoredBoard()
    {
        var board = new StateEnvelope<CoordinationDocument>();
        void Apply(RelayCaller caller, string operation, JsonObject args, int seconds = 0)
        {
            args["repositoryId"] = "repo";
            args["requestId"] ??= Guid.NewGuid().ToString("N");
            var reply = CoordinationStateMachine.Apply(board, AggregateKinds.Board, AggregateKinds.BoardKey("repo", AggregateKinds.DefaultBoard), caller, operation, args, Now.AddSeconds(seconds), ordersEnabled: true);
            Assert.True(reply.Ok, $"{operation}: {reply.Code} {reply.DataJson}");
        }
        var workflow = new JsonObject
        {
            ["state"] = "ready",
            ["packet"] = new JsonObject
            {
                ["goal"] = "Deploy the stylesheet", ["target"] = "repo at revision abc", ["authorityRef"] = "human-grant:17", ["restoration"] = "Restore the previous stylesheet",
                ["stopCondition"] = "Stop if the target differs", ["resources"] = new JsonArray("rig-1")
            },
            ["criteria"] = new JsonArray(new JsonObject { ["id"] = "live", ["description"] = "The new stylesheet is live", ["result"] = "not_run" }),
            ["blockers"] = new JsonArray()
        };
        Apply(Writer, RelayTools.TaskUpdate, new() { ["taskId"] = "plan", ["expectedVersion"] = 0, ["workflow"] = workflow });
        Apply(Writer, RelayTools.TaskUpdate, new() { ["taskId"] = "t", ["expectedVersion"] = 0 });
        Apply(Writer, RelayTools.TaskClaim, new() { ["taskId"] = "t", ["sessionId"] = "a", ["expectedVersion"] = 1, ["leaseSeconds"] = 600 });
        Apply(Writer, RelayTools.ActionBegin, new() { ["taskId"] = "t", ["sessionId"] = "a", ["expectedVersion"] = 2, ["actionId"] = "deploy", ["inputFingerprint"] = "f" });
        Apply(Administrator, RelayTools.ActionReport, new() { ["taskId"] = "t", ["sessionId"] = "admin-session", ["expectedVersion"] = 3, ["actionId"] = "deploy", ["attempt"] = 1, ["recover"] = true }, 60);
        Apply(Writer, RelayTools.ActionReport, new() { ["taskId"] = "t", ["sessionId"] = "a", ["expectedVersion"] = 4, ["actionId"] = "deploy", ["attempt"] = 1, ["outcome"] = "completed", ["report"] = "Deployed after all." }, 90);
        Apply(Writer, RelayTools.SendMessage, new() { ["taskId"] = "t", ["sessionId"] = "a", ["text"] = "Deployed; please review." }, 100);
        Apply(Administrator, RelayTools.StandingPut, new()
        {
            ["expectedRevision"] = 0, ["lines"] = new JsonArray(new JsonObject { ["id"] = "css", ["text"] = "Never deploy CSS without a restoration receipt." }),
            ["grants"] = new JsonArray(new JsonObject { ["id"] = "rigs", ["scope"] = "resource", ["targets"] = new JsonArray("rig-1"), ["expiresAt"] = Now.AddDays(1).ToString("O") })
        }, 110);
        Apply(Writer, RelayTools.StandingPropose, new() { ["sessionId"] = "a", ["line"] = new JsonObject { ["text"] = "Report before releasing a rig." } }, 120);
        Apply(Administrator, RelayTools.OrderPut, new()
        {
            ["orderId"] = "o-1", ["trigger"] = new JsonObject { ["on"] = OrderTriggers.WorkflowCompleted, ["taskId"] = "plan" },
            ["actions"] = new JsonArray(new JsonObject { ["do"] = OrderActions.ReleaseClaims }), ["defaultExpiryDays"] = 7
        }, 130);

        var store = new SqliteStore(Path.Combine(Path.GetTempPath(), "cocern-key-paths-" + Guid.NewGuid().ToString("N"), "relay.db"));
        var grain = GrainId.Create("cocern.board", AggregateKinds.BoardKey("repo", AggregateKinds.DefaultBoard));
        await new SqliteGrainStorage(store).WriteStateAsync(AggregateKinds.Board, grain, new GrainState<StateEnvelope<CoordinationDocument>>(board));
        var envelope = JsonNode.Parse((await store.ReadAsync(AggregateKinds.Board, grain, CancellationToken.None))!.Payload!)!;
        return (new(envelope, JsonNode.Parse(envelope["Value"]!["Json"]!.GetValue<string>())!.AsObject()), store);
    }

    private static readonly Regex PathLiteral = new(@"'\$[^']*'");
    private const string Source = @"(?<source>[A-Za-z_]\w*(?:\.[A-Za-z_]\w*)?)";
    private static readonly Regex Arrow = new(Source + @"\s*->>?\s*'(?<path>\$[^']*)'");
    private static readonly Regex Extract = new(@"json_extract(?:_string)?\(\s*" + Source + @"\s*,\s*'(?<path>\$[^']*)'\s*\)", RegexOptions.IgnoreCase);
    private static readonly Regex Each = new(@"json_each\(\s*" + Source + @"\s*->>?\s*'(?<path>\$[^']*)'\s*\)\s+(?:AS\s+)?(?<alias>[A-Za-z_]\w*)", RegexOptions.IgnoreCase);
    private static readonly Regex Segment = new(@"\G(?:\.(?<key>[A-Za-z_]\w*)|\[\*\])");

    /// <summary>
    /// Finds every JSON path the SQL reads (<c>x -&gt; '$..'</c>, <c>x -&gt;&gt; '$..'</c> and <c>json_extract[_string](x, '$..')</c>), works
    /// out what each reads it from, and asserts each resolves to a value in the fixture. Returns the number of distinct paths.
    /// </summary>
    private static int AssertPathsResolve(string sql, Fixture fixture)
    {
        sql = Regex.Replace(sql, "--[^\n]*", "");
        var reads = Arrow.Matches(sql).Concat(Extract.Matches(sql))
            .Select(match => (Source: match.Groups["source"].Value, Path: match.Groups["path"].Value, At: match.Groups["path"].Index)).ToList();
        // Every path literal is one of those reads, so a read written some other way cannot slip past unchecked.
        Assert.Equal(PathLiteral.Matches(sql).Select(literal => literal.Index + 1).Order(), reads.Select(read => read.At).Order());
        // A json_each alias's value is one entry of the array (or object) it walks.
        var aliases = Each.Matches(sql).ToDictionary(match => match.Groups["alias"].Value, match => (Source: match.Groups["source"].Value, Path: match.Groups["path"].Value));
        IReadOnlyList<JsonNode?> From(string source)
        {
            var dot = source.IndexOf('.');
            var (qualifier, column) = dot < 0 ? ("", source) : (source[..dot], source[(dot + 1)..]);
            if (column == "value" && aliases.TryGetValue(qualifier, out var each)) return Resolve(From(each.Source), each.Path + "[*]");
            return column switch
            {
                // grain_state.payload: the envelope as storage wrote it.
                "payload" => [fixture.Envelope],
                // The board document (board_doc.doc; the standing read calls it document).
                "doc" or "document" => [fixture.Board],
                // One task (task_doc.task): each of the board's tasks.
                "task" => fixture.Board["tasks"]!.AsObject().Select(entry => entry.Value).ToList(),
                _ => throw new InvalidOperationException($"The SQL reads JSON from {source}, which this test does not know; add it to the fixture.")
            };
        }
        var unresolved = reads.Where(read => !Resolve(From(read.Source), read.Path).Any(node => node is not null))
            .Select(read => $"{read.Source} {read.Path}").Distinct().ToArray();
        Assert.Empty(unresolved);
        return reads.Select(read => (read.Source, read.Path)).Distinct().Count();
    }

    /// <summary>The values at <paramref name="path"/> (<c>$</c>, then <c>.key</c> or <c>[*]</c> steps) under each of <paramref name="roots"/>.</summary>
    private static IReadOnlyList<JsonNode?> Resolve(IEnumerable<JsonNode?> roots, string path)
    {
        Assert.StartsWith("$", path);
        var current = roots.ToList();
        for (var position = 1; position < path.Length;)
        {
            var segment = Segment.Match(path, position);
            Assert.True(segment.Success, $"Unsupported JSON path syntax at {position} in {path}.");
            current = segment.Groups["key"].Success
                ? current.OfType<JsonObject>().Select(node => node[segment.Groups["key"].Value]).ToList()
                : current.SelectMany(node => node switch { JsonArray array => array.ToList(), JsonObject entries => entries.Select(entry => entry.Value).ToList(), _ => [] }).ToList();
            position += segment.Length;
        }
        return current;
    }
}
