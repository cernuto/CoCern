using CoCern.Contracts.Dashboard;

namespace CoCern.Dashboard.Services;

internal sealed record DashboardStation(string MachineId, string Name, bool Enrolled, bool Reachable, DashboardSession[] Sessions)
{
    public int SessionCount => Sessions.Length;
}

/// <summary>Groups display cards without changing the machine identities used for authorization or routing.</summary>
internal static class DashboardStations
{
    public static DashboardStation[] Build(DashboardSnapshot snapshot)
    {
        var enrolled = snapshot.Machines.ToDictionary(machine => machine.MachineId, machine => new Station(machine), StringComparer.Ordinal);
        var aliases = new Dictionary<string, Station?>(StringComparer.OrdinalIgnoreCase);
        foreach (var station in enrolled.Values)
        {
            AddAlias(station.MachineId, station);
            AddAlias(station.Name, station);
        }

        var reported = new Dictionary<string, Station>(StringComparer.OrdinalIgnoreCase);
        foreach (var session in snapshot.Sessions)
        {
            var id = string.IsNullOrWhiteSpace(session.MachineId) ? "" : session.MachineId;
            // An enrolled identity always wins. A hostname is a display alias only when it identifies one enrollment.
            if (!enrolled.TryGetValue(id, out var station) && (!aliases.TryGetValue(id, out station) || station is null))
            {
                if (!reported.TryGetValue(id, out station))
                {
                    station = new Station(id, DashboardFormat.Machine(session));
                    reported.Add(id, station);
                }
            }
            station.Sessions.Add(session);
        }

        return enrolled.Values.Concat(reported.Values)
            .Select(station => new DashboardStation(station.MachineId, station.Name, station.Enrolled, station.Reachable,
                station.Sessions.OrderBy(session => session.SessionId, StringComparer.Ordinal).ToArray()))
            .OrderBy(station => station.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(station => station.MachineId, StringComparer.Ordinal).ToArray();

        void AddAlias(string alias, Station station)
        {
            if (string.IsNullOrWhiteSpace(alias)) return;
            if (!aliases.TryAdd(alias, station) && !ReferenceEquals(aliases[alias], station)) aliases[alias] = null;
        }
    }

    private sealed class Station(string machineId, string name)
    {
        public Station(DashboardMachine machine) : this(machine.MachineId, machine.Name)
        {
            Enrolled = true;
            Reachable = machine.Reachable;
        }

        public string MachineId { get; } = machineId;
        public string Name { get; } = name;
        public bool Enrolled { get; }
        public bool Reachable { get; }
        public List<DashboardSession> Sessions { get; } = [];
    }
}
