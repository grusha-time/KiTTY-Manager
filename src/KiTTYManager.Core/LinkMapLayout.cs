namespace KiTTYManager.Core;

public sealed record LinkMapNode(
    Guid ServerId, string Name, string GroupPath, double X, double Y, int LinkCount);

public sealed record LinkMapEdge(
    Guid ServerAId, Guid ServerBId, bool IsAvailable, DateTimeOffset? LastSuccessUtc);

public sealed record LinkMapModel(
    IReadOnlyList<LinkMapNode> Nodes, IReadOnlyList<LinkMapEdge> Edges,
    double Width, double Height);

public static class LinkMapLayout
{
    private const double NodeWidth = 190;
    private const double NodeHeight = 58;
    private const double ComponentGap = 180;
    private const double NodeGap = 24;

    public static IReadOnlyList<double> HighlightOffsets(int edgeCount)
    {
        if (edgeCount <= 0) return [];
        var step = edgeCount <= 1 ? 0 : Math.Min(28, 140d / (edgeCount - 1));
        return Enumerable.Range(0, edgeCount)
            .Select(index => (index - (edgeCount - 1) / 2d) * step)
            .ToArray();
    }

    public static LinkMapModel Build(ManagerConfig config)
    {
        var servers = config.AllServers().ToDictionary(server => server.Id);
        var edges = config.Links
            .Where(link => link.FromServerId != link.ToServerId &&
                           servers.ContainsKey(link.FromServerId) &&
                           servers.ContainsKey(link.ToServerId))
            .GroupBy(link => OrderedPair(link.FromServerId, link.ToServerId))
            .Select(group => new LinkMapEdge(
                group.Key.A, group.Key.B,
                group.Any(link => link.LastSuccessUtc is not null),
                group.Max(link => link.LastSuccessUtc)))
            .OrderBy(edge => servers[edge.ServerAId].Name, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(edge => servers[edge.ServerBId].Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
        if (edges.Length == 0) return new LinkMapModel([], [], 800, 500);

        var included = edges.SelectMany(edge => new[] { edge.ServerAId, edge.ServerBId }).ToHashSet();
        var adjacency = included.ToDictionary(id => id, _ => new HashSet<Guid>());
        foreach (var edge in edges)
        {
            adjacency[edge.ServerAId].Add(edge.ServerBId);
            adjacency[edge.ServerBId].Add(edge.ServerAId);
        }

        var components = Components(included, adjacency, servers);
        var placed = new Dictionary<Guid, (double X, double Y)>();
        var componentLayouts = components.Select(component =>
        {
            var local = PlaceComponent(component, adjacency, servers);
            var width = local.Values.Max(point => point.X) - local.Values.Min(point => point.X) + NodeWidth;
            var height = local.Values.Max(point => point.Y) - local.Values.Min(point => point.Y) + NodeHeight;
            return (Points: local, Width: Math.Max(width, 420), Height: Math.Max(height, 300));
        }).ToArray();

        var totalArea = componentLayouts.Sum(item =>
            (item.Width + ComponentGap) * (item.Height + ComponentGap));
        var targetRowWidth = Math.Max(900, Math.Sqrt(totalArea) * 1.35);
        var cursorX = ComponentGap;
        var cursorY = ComponentGap;
        var rowHeight = 0d;
        var maxX = 0d;
        foreach (var component in componentLayouts)
        {
            if (cursorX > ComponentGap && cursorX + component.Width > targetRowWidth)
            {
                cursorX = ComponentGap;
                cursorY += rowHeight + ComponentGap;
                rowHeight = 0;
            }
            var minX = component.Points.Values.Min(point => point.X);
            var minY = component.Points.Values.Min(point => point.Y);
            foreach (var (id, point) in component.Points)
                placed[id] = (cursorX + point.X - minX, cursorY + point.Y - minY);
            cursorX += component.Width + ComponentGap;
            rowHeight = Math.Max(rowHeight, component.Height);
            maxX = Math.Max(maxX, cursorX);
        }

        var nodes = included.Select(id =>
            {
                var server = servers[id];
                var point = placed[id];
                return new LinkMapNode(id, server.Name, GroupPath(config, id),
                    point.X, point.Y, adjacency[id].Count);
            })
            .OrderBy(node => node.Name, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(node => node.ServerId)
            .ToArray();
        var height = nodes.Max(node => node.Y) + NodeHeight + ComponentGap;
        return new LinkMapModel(nodes, edges, Math.Max(900, maxX), Math.Max(500, height));
    }

    private static Dictionary<Guid, (double X, double Y)> PlaceComponent(
        IReadOnlyList<Guid> component,
        IReadOnlyDictionary<Guid, HashSet<Guid>> adjacency,
        IReadOnlyDictionary<Guid, ManagedServer> servers)
    {
        var root = component
            .OrderByDescending(id => adjacency[id].Count)
            .ThenBy(id => servers[id].Name, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(id => id)
            .First();
        var result = new Dictionary<Guid, (double X, double Y)>();
        if (component.Count == 1)
        {
            result[root] = (0, 0);
            return result;
        }

        // Самый связанный узел — в центре. Остальные кольца соответствуют
        // кратчайшему расстоянию от него в графе: периферийный сервер оказывается
        // снаружи рядом со своим родителем, а не на случайном участке окружности.
        // Расстояние между центрами соседних карточек всегда больше диагонали
        // карточки с отступом, поэтому прямоугольники физически не пересекаются.
        result[root] = (0, 0);
        var levels = BreadthFirstLevels(root, adjacency, servers);
        var clearance = Math.Sqrt(
            Math.Pow(NodeWidth + NodeGap, 2) +
            Math.Pow(NodeHeight + NodeGap, 2));
        var ringSpacing = Math.Ceiling(clearance + NodeGap);
        var previousRadius = 0d;
        foreach (var level in levels.Values.Distinct().Where(level => level > 0).Order())
        {
            var ringNodes = levels
                .Where(item => item.Value == level)
                .Select(item => item.Key)
                .ToArray();
            // На внешних уровнях оставляем свободные угловые позиции. Благодаря
            // этому дочерний узел может занять место около своего родителя,
            // вместо обязательного равномерного растягивания по всей окружности.
            var slotCount = level == 1
                ? ringNodes.Length
                : Math.Max(ringNodes.Length * 2, 16);
            var requiredRadius = slotCount <= 1
                ? ringSpacing
                : clearance / (2 * Math.Sin(Math.PI / slotCount));
            var radius = Math.Max(previousRadius + ringSpacing, Math.Ceiling(requiredRadius));
            var angles = RingAngles(ringNodes, slotCount, result, adjacency, servers);
            foreach (var id in ringNodes)
            {
                var angle = angles[id];
                result[id] = (
                    radius * Math.Cos(angle),
                    radius * Math.Sin(angle));
            }
            previousRadius = radius;
        }
        return result;
    }

    private static Dictionary<Guid, int> BreadthFirstLevels(
        Guid root,
        IReadOnlyDictionary<Guid, HashSet<Guid>> adjacency,
        IReadOnlyDictionary<Guid, ManagedServer> servers)
    {
        var levels = new Dictionary<Guid, int> { [root] = 0 };
        var queue = new Queue<Guid>();
        queue.Enqueue(root);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            foreach (var next in adjacency[current]
                         .Where(id => !levels.ContainsKey(id))
                         .OrderByDescending(id => adjacency[id].Count)
                         .ThenBy(id => servers[id].Name, StringComparer.CurrentCultureIgnoreCase)
                         .ThenBy(id => id))
            {
                levels[next] = levels[current] + 1;
                queue.Enqueue(next);
            }
        }
        return levels;
    }

    private static Dictionary<Guid, double> RingAngles(
        IReadOnlyList<Guid> ringNodes,
        int slotCount,
        IReadOnlyDictionary<Guid, (double X, double Y)> placed,
        IReadOnlyDictionary<Guid, HashSet<Guid>> adjacency,
        IReadOnlyDictionary<Guid, ManagedServer> servers)
    {
        if (ringNodes.Count == 1)
        {
            var id = ringNodes[0];
            return new Dictionary<Guid, double>
            {
                [id] = DesiredAngle(id, placed, adjacency) ?? -Math.PI / 2
            };
        }

        var desired = ringNodes.ToDictionary(
            id => id,
            id => DesiredAngle(id, placed, adjacency));
        if (desired.Values.All(angle => angle is null))
        {
            return ringNodes
                .OrderByDescending(id => adjacency[id].Count)
                .ThenBy(id => servers[id].Name, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(id => id)
                .Select((id, index) => (id,
                    angle: -Math.PI / 2 + 2 * Math.PI * index / ringNodes.Count))
                .ToDictionary(item => item.id, item => item.angle);
        }

        var slots = Enumerable.Range(0, slotCount)
            .Select(index => -Math.PI / 2 + 2 * Math.PI * index / slotCount)
            .ToArray();
        var freeSlots = Enumerable.Range(0, slotCount).ToHashSet();
        var result = new Dictionary<Guid, double>();

        // Сначала фиксируем узлы с известным направлением к уже размещённым
        // соседям. Каждый получает ближайшую ещё свободную безопасную позицию.
        foreach (var id in ringNodes
            .Where(id => desired[id] is not null)
            .OrderBy(id => NormalizeAngle(desired[id]!.Value))
            .ThenByDescending(id => adjacency[id].Count)
            .ThenBy(id => servers[id].Name, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(id => id))
        {
            var slot = freeSlots
                .OrderBy(index => CircularDistance(slots[index], desired[id]!.Value))
                .ThenBy(index => index)
                .First();
            result[id] = slots[slot];
            freeSlots.Remove(slot);
        }

        // Узлы без родительского направления занимают самые большие оставшиеся
        // промежутки, чтобы не образовывать новый плотный комок.
        foreach (var id in ringNodes
            .Where(id => desired[id] is null)
            .OrderByDescending(id => adjacency[id].Count)
            .ThenBy(id => servers[id].Name, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(id => id))
        {
            var occupied = result.Values.ToArray();
            var slot = freeSlots
                .OrderByDescending(index => occupied.Length == 0
                    ? Math.PI
                    : occupied.Min(angle => CircularDistance(slots[index], angle)))
                .ThenBy(index => index)
                .First();
            result[id] = slots[slot];
            freeSlots.Remove(slot);
        }
        return result;
    }

    private static double? DesiredAngle(
        Guid id,
        IReadOnlyDictionary<Guid, (double X, double Y)> placed,
        IReadOnlyDictionary<Guid, HashSet<Guid>> adjacency)
    {
        var angles = adjacency[id]
            .Where(placed.ContainsKey)
            .Where(parent =>
            {
                var point = placed[parent];
                return Math.Abs(point.X) > 0.000001 || Math.Abs(point.Y) > 0.000001;
            })
            .Select(parent =>
            {
                var point = placed[parent];
                return Math.Atan2(point.Y, point.X);
            })
            .ToArray();
        if (angles.Length == 0) return null;
        return Math.Atan2(angles.Sum(Math.Sin), angles.Sum(Math.Cos));
    }

    private static double NormalizeAngle(double angle)
    {
        var normalized = angle % (2 * Math.PI);
        return normalized < 0 ? normalized + 2 * Math.PI : normalized;
    }

    private static double CircularDistance(double left, double right) =>
        Math.Abs(Math.Atan2(Math.Sin(left - right), Math.Cos(left - right)));

    private static IReadOnlyList<IReadOnlyList<Guid>> Components(
        IReadOnlySet<Guid> included,
        IReadOnlyDictionary<Guid, HashSet<Guid>> adjacency,
        IReadOnlyDictionary<Guid, ManagedServer> servers)
    {
        var remaining = included.ToHashSet();
        var result = new List<IReadOnlyList<Guid>>();
        while (remaining.Count > 0)
        {
            var start = remaining
                .OrderBy(id => servers[id].Name, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(id => id).First();
            var queue = new Queue<Guid>();
            var component = new List<Guid>();
            queue.Enqueue(start);
            remaining.Remove(start);
            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                component.Add(current);
                foreach (var next in adjacency[current]
                             .Where(remaining.Contains)
                             .OrderBy(id => servers[id].Name, StringComparer.CurrentCultureIgnoreCase)
                             .ThenBy(id => id))
                {
                    remaining.Remove(next);
                    queue.Enqueue(next);
                }
            }
            result.Add(component);
        }
        return result;
    }

    private static (Guid A, Guid B) OrderedPair(Guid left, Guid right) =>
        left.CompareTo(right) <= 0 ? (left, right) : (right, left);

    private static string GroupPath(ManagerConfig config, Guid serverId)
    {
        foreach (var group in config.Groups)
        {
            var path = GroupPath(group, serverId, group.Name);
            if (path is not null) return path;
        }
        return "Без группы";
    }

    private static string? GroupPath(ServerGroup group, Guid serverId, string path)
    {
        if (group.Servers.Any(server => server.Id == serverId)) return path;
        foreach (var child in group.Groups)
        {
            var nested = GroupPath(child, serverId, $"{path} / {child.Name}");
            if (nested is not null) return nested;
        }
        return null;
    }
}
