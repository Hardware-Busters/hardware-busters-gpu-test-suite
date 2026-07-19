using GpuSuite.Calibration.Schema;

namespace GpuSuite.Calibration.Graph;

/// <summary>In-progress graph state for one calibration session.</summary>
public sealed class GraphSession
{
    public string Game { get; init; } = "";
    public EnvFingerprint Env { get; init; } = new();
    public MenuGraph? Prior { get; init; }
    public List<MenuNode> Nodes { get; } = new();
    public List<MenuEdge> Edges { get; } = new();
    public string? LastNodeId { get; set; }
}

/// <summary>
/// Assembles the per-game menu graph from a session's observations (module 5). Nodes are screens with
/// their detected controls; edges are the input transitions between them. Incremental: a fresh exploration
/// can be merged into a prior graph to update it after drift, preserving the structure.
/// </summary>
public interface IMenuGraphBuilder
{
    GraphSession BeginGraph(string game, EnvFingerprint env, MenuGraph? prior);
    /// <summary>Add a screen node and (if there was a previous node) the transition that reached it.</summary>
    void AddObservation(GraphSession session, string? inputFromPrevious, MenuNode node);
    /// <summary>Explicitly record a transition edge between two known nodes.</summary>
    void LinkTransition(GraphSession session, string fromNode, string input, string toNode, bool reproducible, Confidence confidence);
    MenuGraph Build(GraphSession session);
    /// <summary>Union a fresh graph into an existing one (node id is the merge key); fresh nodes/edges win.</summary>
    MenuGraph MergeInto(MenuGraph existing, MenuGraph fresh);
}

/// <summary>Default deterministic graph builder. No model calls — pure structural assembly.</summary>
public sealed class MenuGraphBuilder : IMenuGraphBuilder
{
    public GraphSession BeginGraph(string game, EnvFingerprint env, MenuGraph? prior)
        => new() { Game = game, Env = env, Prior = prior };

    public void AddObservation(GraphSession session, string? inputFromPrevious, MenuNode node)
    {
        if (session.Nodes.All(n => !string.Equals(n.Id, node.Id, StringComparison.OrdinalIgnoreCase)))
            session.Nodes.Add(node);

        if (session.LastNodeId is not null && inputFromPrevious is not null)
        {
            session.Edges.Add(new MenuEdge
            {
                From = session.LastNodeId,
                Input = inputFromPrevious,
                To = node.Id,
                Reproducible = false,                 // single observation — reproducibility is proven on re-walk
                Confidence = node.Confidence
            });
        }
        session.LastNodeId = node.Id;
    }

    public void LinkTransition(GraphSession session, string fromNode, string input, string toNode, bool reproducible, Confidence confidence)
        => session.Edges.Add(new MenuEdge { From = fromNode, Input = input, To = toNode, Reproducible = reproducible, Confidence = confidence });

    public MenuGraph Build(GraphSession session)
    {
        var graph = new MenuGraph
        {
            Env = session.Env,
            Version = session.Env.Key(),
            Nodes = session.Nodes.ToList(),
            Edges = session.Edges.ToList()
        };
        return session.Prior is null ? graph : MergeInto(session.Prior, graph);
    }

    public MenuGraph MergeInto(MenuGraph existing, MenuGraph fresh)
    {
        var nodes = existing.Nodes.ToDictionary(n => n.Id, StringComparer.OrdinalIgnoreCase);
        foreach (var n in fresh.Nodes) nodes[n.Id] = n;        // fresh wins

        var edgeKey = (MenuEdge e) => $"{e.From}|{e.Input}|{e.To}".ToLowerInvariant();
        var edges = existing.Edges.ToDictionary(edgeKey, e => e);
        foreach (var e in fresh.Edges) edges[edgeKey(e)] = e;

        return new MenuGraph
        {
            Env = fresh.Env,
            Version = fresh.Env.Key(),
            Nodes = nodes.Values.ToList(),
            Edges = edges.Values.ToList()
        };
    }
}
