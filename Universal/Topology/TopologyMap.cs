using System.Text.Json;

namespace BatCave.Universal.Topology;

internal sealed class TopologyMap
{
    private readonly IReadOnlyDictionary<string, TopologyDisplay> _displaysById;
    private readonly IReadOnlyList<MaterializedLink> _links;

    private TopologyMap(
        string sourcePath,
        string localDeviceId,
        IReadOnlyList<TopologyDisplay> displays,
        IReadOnlyList<MaterializedLink> links)
    {
        SourcePath = sourcePath;
        LocalDeviceId = localDeviceId;
        Displays = displays;
        _displaysById = displays.ToDictionary(display => display.Id, StringComparer.OrdinalIgnoreCase);
        _links = links;
    }

    public string SourcePath { get; }

    public string LocalDeviceId { get; }

    public IReadOnlyList<TopologyDisplay> Displays { get; }

    public IReadOnlyList<MaterializedLink> Links => _links;

    public static bool TryLoadForLocalDevice(string localDeviceId, out TopologyMap? topology, out string status)
    {
        var path = FindTopologyFile();
        if (path is null)
        {
            topology = null;
            status = "topology=missing path=Universal/topology.local.json";
            return false;
        }

        return TryLoad(path, localDeviceId, out topology, out status);
    }

    public static bool TryLoad(string path, string localDeviceId, out TopologyMap? topology, out string status)
    {
        topology = null;

        try
        {
            if (new FileInfo(path).Length > 262144) throw new InvalidDataException("Topology exceeds 256 KiB.");
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            if (!root.TryGetProperty("schema", out var schema) || schema.GetString() != "batcave.topology.v1")
                throw new InvalidDataException("Unknown topology schema.");
            var displays = ReadDisplays(root);
            if (displays.Count is < 2 or > 64 || !displays.Any(d => d.DeviceId == localDeviceId)
                || displays.Select(d => d.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != displays.Count)
                throw new InvalidDataException("Invalid topology display inventory.");
            foreach (var display in displays)
            {
                var b = display.Bounds;
                if (b.Width < 2 || b.Height < 2 || b.Width > 100000 || b.Height > 100000
                    || Math.Abs((long)b.Left) > 1000000 || Math.Abs((long)b.Top) > 1000000
                    || (long)b.Left + b.Width - 1 != b.Right || (long)b.Top + b.Height - 1 != b.Bottom)
                    throw new InvalidDataException("Invalid topology display bounds.");
            }
            var displayLookup = displays.ToDictionary(display => display.Id, StringComparer.OrdinalIgnoreCase);
            var links = ReadLinks(root, displayLookup);
            if (links.Count is < 1 or > 128) throw new InvalidDataException("Invalid topology links.");

            topology = new TopologyMap(path, localDeviceId, displays, links);
            var localSourceLinks = topology.LocalSourceLinks(localDeviceId).Count();
            status = $"topology=loaded path={path} displays={displays.Count} links={links.Count} localSourceLinks={localSourceLinks}";
            return true;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or InvalidOperationException or KeyNotFoundException or ArgumentException or FormatException or OverflowException)
        {
            status = $"topology=invalid path={path} error={ex.Message}";
            return false;
        }
    }

    public bool TryMapCrossing(
        Point2D from,
        Point2D to,
        string localDeviceId,
        out TopologyHandoff handoff)
    {
        handoff = default;
        var movement = to - from;
        if (movement.LengthSquared < 0.0001)
        {
            return false;
        }

        foreach (var link in _links)
        {
            if (!TryGetLocalSource(link, localDeviceId, out var source, out var target))
            {
                continue;
            }

            if (!source.TryIntersect(from, to, movement, out var sourceT, out var sourcePoint, out var sourceSelection))
            {
                continue;
            }

            var targetPoint = target.PointAt(sourceT);
            var targetSelection = target.SelectionAt(sourceT);
            var targetDisplay = _displaysById[targetSelection.DisplayId];

            handoff = new TopologyHandoff(
                link.Id,
                sourceT,
                sourceSelection.DisplayId,
                sourceSelection.Edge,
                sourceSelection.Position,
                (int)Math.Round(sourcePoint.X),
                (int)Math.Round(sourcePoint.Y),
                targetDisplay.DeviceId,
                targetSelection.DisplayId,
                targetSelection.Edge,
                targetSelection.Position,
                (int)Math.Round(targetPoint.X),
                (int)Math.Round(targetPoint.Y));
            return true;
        }

        return false;
    }

    public bool IsSafelyAwayFromLocalSourceSegments(Point2D point, string localDeviceId, double minimumDistance)
    {
        foreach (var link in _links)
        {
            if (!TryGetLocalSource(link, localDeviceId, out var source, out _))
            {
                continue;
            }

            if (source.DistanceToSegments(point) < minimumDistance)
            {
                return false;
            }
        }

        return true;
    }

    public bool HasLocalSourceLinks(string localDeviceId)
    {
        return LocalSourceLinks(localDeviceId).Any();
    }

    private IEnumerable<MaterializedLink> LocalSourceLinks(string localDeviceId)
    {
        return _links.Where(link => TryGetLocalSource(link, localDeviceId, out _, out _));
    }

    private bool TryGetLocalSource(
        MaterializedLink link,
        string localDeviceId,
        out MaterializedSide source,
        out MaterializedSide target)
    {
        if (SideBelongsToDevice(link.From, localDeviceId))
        {
            source = link.From;
            target = link.To;
            return true;
        }

        if (SideBelongsToDevice(link.To, localDeviceId))
        {
            source = link.To;
            target = link.From;
            return true;
        }

        source = default;
        target = default;
        return false;
    }

    private bool SideBelongsToDevice(MaterializedSide side, string deviceId)
    {
        return DisplayBelongsToDevice(side.Start.Selection.DisplayId, deviceId)
            && DisplayBelongsToDevice(side.End.Selection.DisplayId, deviceId);
    }

    private bool DisplayBelongsToDevice(string displayId, string deviceId)
    {
        return _displaysById.TryGetValue(displayId, out var display)
            && string.Equals(display.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<TopologyDisplay> ReadDisplays(JsonElement root)
    {
        if (!root.TryGetProperty("displays", out var displaysElement) || displaysElement.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<TopologyDisplay>();
        }

        var displays = new List<TopologyDisplay>();
        foreach (var display in displaysElement.EnumerateArray())
        {
            var bounds = display.GetProperty("bounds");
            displays.Add(
                new TopologyDisplay(
                    ReadRequiredString(display, "id"),
                    ReadRequiredString(display, "deviceId"),
                    ReadString(display, "label"),
                    ReadString(display, "kind"),
                    new TopologyBounds(
                        ReadInt(bounds, "left"),
                        ReadInt(bounds, "top"),
                        ReadInt(bounds, "width"),
                        ReadInt(bounds, "height"),
                        ReadInt(bounds, "right"),
                        ReadInt(bounds, "bottom"))));
        }

        return displays;
    }

    private static IReadOnlyList<MaterializedLink> ReadLinks(
        JsonElement root,
        IReadOnlyDictionary<string, TopologyDisplay> displays)
    {
        if (!root.TryGetProperty("links", out var linksElement) || linksElement.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<MaterializedLink>();
        }

        var links = new List<MaterializedLink>();
        foreach (var link in linksElement.EnumerateArray())
        {
            var from = link.GetProperty("from");
            var to = link.GetProperty("to");
            var fromStart = ReadSelection(from.GetProperty("start"));
            var fromEnd = ReadSelection(from.GetProperty("end"));
            var toStart = ReadSelection(to.GetProperty("start"));
            var toEnd = ReadSelection(to.GetProperty("end"));
            foreach (var side in new[] { (fromStart, fromEnd), (toStart, toEnd) })
            {
                if (side.Item1.Edge != side.Item2.Edge || displays[side.Item1.DisplayId].DeviceId != displays[side.Item2.DisplayId].DeviceId
                    || side.Item1 == side.Item2)
                    throw new InvalidDataException("Invalid topology link side.");
            }
            if (displays[fromStart.DisplayId].DeviceId == displays[toStart.DisplayId].DeviceId)
                throw new InvalidDataException("Topology links must connect different devices.");
            links.Add(
                new MaterializedLink(
                    ReadString(link, "id"),
                    MaterializedSide.Create(fromStart, fromEnd, displays),
                    MaterializedSide.Create(toStart, toEnd, displays)));
        }

        return links;
    }

    private static MaterializedSelection Materialize(
        TopologyEdgeSelection selection,
        IReadOnlyDictionary<string, TopologyDisplay> displays)
    {
        var display = displays[selection.DisplayId];
        return new MaterializedSelection(selection, PointOnEdge(display, selection.Edge, selection.Position));
    }

    private static TopologyEdgeSelection ReadSelection(JsonElement selection)
    {
        var edge = ReadRequiredString(selection, "edge").ToLowerInvariant();
        var position = ReadDouble(selection, "position");
        if (edge is not ("left" or "right" or "top" or "bottom") || !double.IsFinite(position) || position < 0 || position > 1)
            throw new InvalidDataException("Invalid topology edge selection.");
        return new TopologyEdgeSelection(ReadRequiredString(selection, "displayId"), edge, position);
    }

    private static TopologyEdgeSelection InterpolateSelection(
        TopologyEdgeSelection start,
        TopologyEdgeSelection end,
        double t,
        Point2D point)
    {
        if (string.Equals(start.DisplayId, end.DisplayId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(start.Edge, end.Edge, StringComparison.OrdinalIgnoreCase))
        {
            return start with
            {
                Position = Math.Clamp(start.Position + ((end.Position - start.Position) * t), 0, 1)
            };
        }

        return t < 0.5
            ? start with { Position = Math.Clamp(start.Position, 0, 1) }
            : end with { Position = Math.Clamp(end.Position, 0, 1) };
    }

    private static Point2D PointOnEdge(TopologyDisplay display, string edge, double position)
    {
        position = Math.Clamp(position, 0, 1);
        return edge switch
        {
            "left" => new Point2D(display.Bounds.Left, display.Bounds.Top + ((display.Bounds.Height - 1) * position)),
            "right" => new Point2D(display.Bounds.Right, display.Bounds.Top + ((display.Bounds.Height - 1) * position)),
            "top" => new Point2D(display.Bounds.Left + ((display.Bounds.Width - 1) * position), display.Bounds.Top),
            "bottom" => new Point2D(display.Bounds.Left + ((display.Bounds.Width - 1) * position), display.Bounds.Bottom),
            _ => throw new InvalidOperationException($"Unknown topology edge: {edge}")
        };
    }

    private static bool SegmentIntersection(
        Point2D p,
        Point2D p2,
        Point2D q,
        Point2D q2,
        out double movementT,
        out double barrierT)
    {
        var r = p2 - p;
        var s = q2 - q;
        var denominator = Cross(r, s);
        if (Math.Abs(denominator) < 0.000001)
        {
            movementT = 0;
            barrierT = 0;
            return false;
        }

        var qp = q - p;
        movementT = Cross(qp, s) / denominator;
        barrierT = Cross(qp, r) / denominator;
        const double epsilon = 0.000001;
        return movementT >= -epsilon
            && movementT <= 1 + epsilon
            && barrierT >= -epsilon
            && barrierT <= 1 + epsilon;
    }

    private static double DistanceToSegment(Point2D point, Point2D start, Point2D end)
    {
        var segment = end - start;
        var lengthSquared = segment.LengthSquared;
        if (lengthSquared < 0.0001)
        {
            return (point - start).Length;
        }

        var t = Math.Clamp(Dot(point - start, segment) / lengthSquared, 0, 1);
        var projection = Lerp(start, end, t);
        return (point - projection).Length;
    }

    private static Point2D Lerp(Point2D start, Point2D end, double t)
    {
        return new Point2D(
            start.X + ((end.X - start.X) * t),
            start.Y + ((end.Y - start.Y) * t));
    }

    private static double Cross(Point2D a, Point2D b)
    {
        return (a.X * b.Y) - (a.Y * b.X);
    }

    private static double Dot(Point2D a, Point2D b)
    {
        return (a.X * b.X) + (a.Y * b.Y);
    }

    private static string? FindTopologyFile()
    {
        foreach (var root in AncestorDirectories(Directory.GetCurrentDirectory()).Concat(AncestorDirectories(AppContext.BaseDirectory)))
        {
            var candidate = Path.Combine(root, "Universal", "topology.local.json");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static IEnumerable<string> AncestorDirectories(string start)
    {
        var directory = new DirectoryInfo(start);
        while (directory is not null)
        {
            yield return directory.FullName;
            directory = directory.Parent;
        }
    }

    private static string ReadRequiredString(JsonElement element, string propertyName)
    {
        var value = ReadString(element, propertyName);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"Missing required topology property: {propertyName}");
        }

        return value;
    }

    private static string ReadString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property) && property.ValueKind != JsonValueKind.Null
            ? property.ToString()
            : string.Empty;
    }

    private static int ReadInt(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property) && property.TryGetInt32(out var value)
            ? value
            : 0;
    }

    private static double ReadDouble(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property) && property.TryGetDouble(out var value)
            ? value
            : 0;
    }
}

internal readonly record struct Point2D(double X, double Y)
{
    public double LengthSquared => (X * X) + (Y * Y);

    public double Length => Math.Sqrt(LengthSquared);

    public static Point2D operator -(Point2D left, Point2D right)
    {
        return new Point2D(left.X - right.X, left.Y - right.Y);
    }
}

internal sealed record TopologyDisplay(
    string Id,
    string DeviceId,
    string Label,
    string Kind,
    TopologyBounds Bounds);

internal sealed record TopologyBounds(int Left, int Top, int Width, int Height, int Right, int Bottom);

internal sealed record TopologyEdgeSelection(string DisplayId, string Edge, double Position);

internal readonly record struct MaterializedSelection(TopologyEdgeSelection Selection, Point2D Point);

internal readonly record struct MaterializedSide(
    MaterializedSelection Start,
    MaterializedSelection End,
    IReadOnlyList<MaterializedSegment> Segments)
{
    public static MaterializedSide Create(
        TopologyEdgeSelection startSelection,
        TopologyEdgeSelection endSelection,
        IReadOnlyDictionary<string, TopologyDisplay> displays)
    {
        var start = Materialize(startSelection, displays);
        var end = Materialize(endSelection, displays);
        var rawSegments = BuildRawSegments(startSelection, endSelection, displays);
        var totalLength = rawSegments.Sum(segment => segment.Length);
        if (totalLength < 0.0001)
        {
            rawSegments = new[] { new RawSegment(start, end) };
            totalLength = rawSegments.Sum(segment => segment.Length);
        }

        var segments = new List<MaterializedSegment>();
        var cumulative = 0.0;
        foreach (var segment in rawSegments)
        {
            var length = segment.Length;
            var segmentStartT = totalLength < 0.0001 ? 0 : cumulative / totalLength;
            cumulative += length;
            var segmentEndT = totalLength < 0.0001 ? 1 : cumulative / totalLength;
            if (Math.Abs(segmentEndT - segmentStartT) < 0.000001)
            {
                continue;
            }

            segments.Add(new MaterializedSegment(segment.Start, segment.End, segmentStartT, segmentEndT));
        }

        if (segments.Count == 0)
        {
            segments.Add(new MaterializedSegment(start, end, 0, 1));
        }

        return new MaterializedSide(start, end, segments);
    }

    public bool TryIntersect(
        Point2D from,
        Point2D to,
        Point2D movement,
        out double sideT,
        out Point2D point,
        out TopologyEdgeSelection selection)
    {
        foreach (var segment in Segments)
        {
            if (!SegmentIntersection(from, to, segment.Start.Point, segment.End.Point, out _, out var segmentT))
            {
                continue;
            }

            var normal = segment.OutwardNormal;
            if (normal.LengthSquared > 0.0001 && Dot(movement, normal) <= 0)
            {
                continue;
            }

            segmentT = Math.Clamp(segmentT, 0, 1);
            sideT = segment.SideTAt(segmentT);
            point = segment.PointAt(segmentT);
            selection = segment.SelectionAt(segmentT);
            return true;
        }

        sideT = 0;
        point = default;
        selection = default!;
        return false;
    }

    public Point2D PointAt(double sideT)
    {
        return SegmentAt(sideT).PointAtLocalSideT(sideT);
    }

    public TopologyEdgeSelection SelectionAt(double sideT)
    {
        return SegmentAt(sideT).SelectionAtLocalSideT(sideT);
    }

    public double DistanceToSegments(Point2D point)
    {
        return Segments.Min(segment => DistanceToSegment(point, segment.Start.Point, segment.End.Point));
    }

    private MaterializedSegment SegmentAt(double sideT)
    {
        sideT = Math.Clamp(sideT, 0, 1);
        foreach (var segment in Segments)
        {
            if (sideT >= segment.StartT - 0.000001 && sideT <= segment.EndT + 0.000001)
            {
                return segment;
            }
        }

        return sideT < 0.5 ? Segments[0] : Segments[^1];
    }

    private static IReadOnlyList<RawSegment> BuildRawSegments(
        TopologyEdgeSelection startSelection,
        TopologyEdgeSelection endSelection,
        IReadOnlyDictionary<string, TopologyDisplay> displays)
    {
        var start = Materialize(startSelection, displays);
        var end = Materialize(endSelection, displays);
        if (string.Equals(startSelection.DisplayId, endSelection.DisplayId, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(startSelection.Edge, endSelection.Edge, StringComparison.OrdinalIgnoreCase))
        {
            return new[] { new RawSegment(start, end) };
        }

        var forward = EdgeAxis(end.Point, startSelection.Edge) >= EdgeAxis(start.Point, startSelection.Edge);
        var startBoundary = forward ? 1.0 : 0.0;
        var endBoundary = forward ? 0.0 : 1.0;
        var startBoundarySelection = startSelection with { Position = startBoundary };
        var endBoundarySelection = endSelection with { Position = endBoundary };

        return new[]
        {
            new RawSegment(start, Materialize(startBoundarySelection, displays)),
            new RawSegment(Materialize(endBoundarySelection, displays), end)
        };
    }

    private static MaterializedSelection Materialize(
        TopologyEdgeSelection selection,
        IReadOnlyDictionary<string, TopologyDisplay> displays)
    {
        var display = displays[selection.DisplayId];
        return new MaterializedSelection(selection, PointOnEdge(display, selection.Edge, selection.Position));
    }

    private static Point2D PointOnEdge(TopologyDisplay display, string edge, double position)
    {
        position = Math.Clamp(position, 0, 1);
        return edge switch
        {
            "left" => new Point2D(display.Bounds.Left, display.Bounds.Top + ((display.Bounds.Height - 1) * position)),
            "right" => new Point2D(display.Bounds.Right, display.Bounds.Top + ((display.Bounds.Height - 1) * position)),
            "top" => new Point2D(display.Bounds.Left + ((display.Bounds.Width - 1) * position), display.Bounds.Top),
            "bottom" => new Point2D(display.Bounds.Left + ((display.Bounds.Width - 1) * position), display.Bounds.Bottom),
            _ => throw new InvalidOperationException($"Unknown topology edge: {edge}")
        };
    }

    private static double EdgeAxis(Point2D point, string edge)
    {
        return edge is "left" or "right" ? point.Y : point.X;
    }

    private static double DistanceToSegment(Point2D point, Point2D start, Point2D end)
    {
        var segment = end - start;
        var lengthSquared = segment.LengthSquared;
        if (lengthSquared < 0.0001)
        {
            return (point - start).Length;
        }

        var t = Math.Clamp(Dot(point - start, segment) / lengthSquared, 0, 1);
        var projection = Lerp(start, end, t);
        return (point - projection).Length;
    }

    private static bool SegmentIntersection(
        Point2D p,
        Point2D p2,
        Point2D q,
        Point2D q2,
        out double movementT,
        out double barrierT)
    {
        var r = p2 - p;
        var s = q2 - q;
        var denominator = Cross(r, s);
        if (Math.Abs(denominator) < 0.000001)
        {
            movementT = 0;
            barrierT = 0;
            return false;
        }

        var qp = q - p;
        movementT = Cross(qp, s) / denominator;
        barrierT = Cross(qp, r) / denominator;
        const double epsilon = 0.000001;
        return movementT >= -epsilon
            && movementT <= 1 + epsilon
            && barrierT >= -epsilon
            && barrierT <= 1 + epsilon;
    }

    private static Point2D Lerp(Point2D start, Point2D end, double t)
    {
        return new Point2D(
            start.X + ((end.X - start.X) * t),
            start.Y + ((end.Y - start.Y) * t));
    }

    private static double Cross(Point2D a, Point2D b)
    {
        return (a.X * b.Y) - (a.Y * b.X);
    }

    private static double Dot(Point2D a, Point2D b)
    {
        return (a.X * b.X) + (a.Y * b.Y);
    }

    private readonly record struct RawSegment(MaterializedSelection Start, MaterializedSelection End)
    {
        public double Length => (End.Point - Start.Point).Length;
    }
}

internal readonly record struct MaterializedSegment(
    MaterializedSelection Start,
    MaterializedSelection End,
    double StartT,
    double EndT)
{
    public Point2D OutwardNormal
    {
        get
        {
            var start = EdgeNormal(Start.Selection.Edge);
            var end = EdgeNormal(End.Selection.Edge);
            var combined = new Point2D(start.X + end.X, start.Y + end.Y);
            return combined.LengthSquared < 0.0001
                ? new Point2D(0, 0)
                : new Point2D(combined.X / combined.Length, combined.Y / combined.Length);
        }
    }

    public double SideTAt(double segmentT)
    {
        return StartT + ((EndT - StartT) * Math.Clamp(segmentT, 0, 1));
    }

    public Point2D PointAt(double segmentT)
    {
        return Lerp(Start.Point, End.Point, Math.Clamp(segmentT, 0, 1));
    }

    public Point2D PointAtLocalSideT(double sideT)
    {
        return PointAt(LocalSegmentT(sideT));
    }

    public TopologyEdgeSelection SelectionAt(double segmentT)
    {
        return InterpolateSelection(Start.Selection, End.Selection, Math.Clamp(segmentT, 0, 1));
    }

    public TopologyEdgeSelection SelectionAtLocalSideT(double sideT)
    {
        return SelectionAt(LocalSegmentT(sideT));
    }

    private double LocalSegmentT(double sideT)
    {
        return Math.Abs(EndT - StartT) < 0.000001
            ? 0
            : Math.Clamp((sideT - StartT) / (EndT - StartT), 0, 1);
    }

    private static TopologyEdgeSelection InterpolateSelection(
        TopologyEdgeSelection start,
        TopologyEdgeSelection end,
        double t)
    {
        if (string.Equals(start.DisplayId, end.DisplayId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(start.Edge, end.Edge, StringComparison.OrdinalIgnoreCase))
        {
            return start with
            {
                Position = Math.Clamp(start.Position + ((end.Position - start.Position) * t), 0, 1)
            };
        }

        return t < 0.5
            ? start with { Position = Math.Clamp(start.Position, 0, 1) }
            : end with { Position = Math.Clamp(end.Position, 0, 1) };
    }

    private static Point2D Lerp(Point2D start, Point2D end, double t)
    {
        return new Point2D(
            start.X + ((end.X - start.X) * t),
            start.Y + ((end.Y - start.Y) * t));
    }

    private static Point2D EdgeNormal(string edge)
    {
        return edge switch
        {
            "left" => new Point2D(-1, 0),
            "right" => new Point2D(1, 0),
            "top" => new Point2D(0, -1),
            "bottom" => new Point2D(0, 1),
            _ => new Point2D(0, 0)
        };
    }
}

internal readonly record struct MaterializedLink(string Id, MaterializedSide From, MaterializedSide To);

internal readonly record struct TopologyHandoff(
    string LinkId,
    double SegmentPosition,
    string SourceDisplayId,
    string SourceEdge,
    double SourcePosition,
    int SourceX,
    int SourceY,
    string TargetDeviceId,
    string TargetDisplayId,
    string TargetEdge,
    double TargetPosition,
    int TargetX,
    int TargetY);
