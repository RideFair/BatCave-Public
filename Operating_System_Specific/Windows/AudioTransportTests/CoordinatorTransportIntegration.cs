using System.Collections.Concurrent;
using System.Net;
using BatCave.Audio;
using BatCave.AudioPreview;
using BatCave.WindowsAgent;

internal static class CoordinatorTransportIntegration
{
    public static async Task Run()
    {
        await using var first = new Node("integration-source-a");
        await using var second = new Node("integration-source-b");
        await using var destination = new Node("integration-destination");
        var nodes = new[] { first, second, destination };
        foreach (var node in nodes) node.Approve(nodes.Where(other => other != node).Select(other => other.Identity.Approval).ToArray());
        await first.Network.Connect("127.0.0.1", destination.Identity.Approval, destination.Network.LocalPort);
        await second.Network.Connect("127.0.0.1", destination.Identity.Approval, destination.Network.LocalPort);
        await Until(() => destination.Coordinator.State.Devices.Length == 3
            && first.Coordinator.State.Devices.Length == 2 && second.Coordinator.State.Devices.Length == 2);
        Require(nodes.All(node => node.Platform.Captures.Count == 0 && node.Platform.Outputs.Count == 0), "TLS discovery started audio without consent.");

        var routes = new[]
        {
            new AudioRoute(first.Identity.Id, new(destination.Identity.Id, "speakers")),
            new AudioRoute(second.Identity.Id, new(destination.Identity.Id, "speakers")),
            new AudioRoute(first.Identity.Id, new(destination.Identity.Id, "headphones"), .5f),
        };
        first.Coordinator.Start(Array.Empty<AudioRoute>(), allowSending: false);
        second.Coordinator.Start(Array.Empty<AudioRoute>(), allowSending: false);
        await Until(() => first.Coordinator.State.IsEnabled && second.Coordinator.State.IsEnabled);
        destination.Coordinator.Start(routes, allowSending: false);
        await Until(() => destination.Coordinator.State.IsEnabled && destination.Platform.LiveOutputs == 2);
        await Task.Delay(30);
        Require(first.Platform.LiveCapture is null && second.Platform.LiveCapture is null, "Receiver subscription bypassed source sending consent.");

        first.Coordinator.Start(routes.Where(route => route.SourceDeviceId == first.Identity.Id), allowSending: true);
        second.Coordinator.Start(routes.Where(route => route.SourceDeviceId == second.Identity.Id), allowSending: true);
        await Until(() => first.Coordinator.State.ActiveSendTargets == 1 && second.Coordinator.State.ActiveSendTargets == 1
            && first.Platform.LiveCapture is not null && second.Platform.LiveCapture is not null);
        var counts = new ConcurrentDictionary<string, int>();
        destination.Network.PacketReceived += (peer, _) => counts.AddOrUpdate(peer.DeviceId, 1, (_, count) => count + 1);
        first.Platform.Emit(.25f, 2); second.Platform.Emit(.5f, 2);
        await Until(() => counts.GetValueOrDefault(first.Identity.Id) == 2 && counts.GetValueOrDefault(second.Identity.Id) == 2);
        await Task.Delay(30); // let the already received PCM drain through the coordinator worker
        Require(destination.Platform.Pull("speakers").All(sample => sample == .75f), "Two authenticated origins did not mix through TLS and coordinator negotiation.");
        Require(destination.Platform.Pull("headphones").All(sample => sample == .125f), "Selected second output lost its independent queue or gain.");
        Require(destination.Platform.Captures.Count == 0 && first.Platform.Outputs.Count == 0 && second.Platform.Outputs.Count == 0,
            "Received PCM was relayed or routed to an unselected source output.");

        var oldOutput = destination.Platform.Outputs["speakers"];
        var oldCapture = first.Platform.LiveCapture!;
        first.Platform.Emit(.25f, 2); second.Platform.Emit(.5f, 2);
        destination.Coordinator.Pause("Integration pause");
        Require(!destination.Coordinator.State.IsEnabled && oldOutput.Pull().All(sample => sample == 0), "Pause did not immediately silence queued PCM.");
        await Until(() => destination.Platform.LiveOutputs == 0 && first.Platform.LiveCapture is null && second.Platform.LiveCapture is null);
        var beforeLateCapture = counts.GetValueOrDefault(first.Identity.Id);
        oldCapture.Emit(.9f); await Task.Delay(40);
        Require(counts.GetValueOrDefault(first.Identity.Id) == beforeLateCapture, "A retired native callback resumed sending after pause.");

        // Reconsent all three nodes and prove a real socket disconnect cancels queued
        // playback immediately and cannot silently resume the remaining source.
        first.Coordinator.Start(Array.Empty<AudioRoute>(), true); second.Coordinator.Start(Array.Empty<AudioRoute>(), true);
        await Until(() => first.Coordinator.State.IsEnabled && second.Coordinator.State.IsEnabled);
        destination.Coordinator.Start(routes, false);
        await Until(() => first.Coordinator.State.ActiveSendTargets == 1 && second.Coordinator.State.ActiveSendTargets == 1
            && first.Platform.LiveCapture is not null && second.Platform.LiveCapture is not null);
        var connectedOutput = destination.Platform.Outputs["speakers"];
        first.Platform.Emit(.25f, 2); second.Platform.Emit(.5f, 2);
        destination.Network.Disconnect(first.Identity.Id);
        Require(!destination.Coordinator.State.IsEnabled && connectedOutput.Pull().All(sample => sample == 0), "TLS disconnect left the mixer enabled.");
        await Until(() => destination.Platform.LiveOutputs == 0 && first.Platform.LiveCapture is null && second.Platform.LiveCapture is null);
    }

    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static async Task Until(Func<bool> condition)
    { using var timeout = new CancellationTokenSource(4000); while (!condition()) await Task.Delay(10, timeout.Token); }

    private sealed class Node : IAsyncDisposable
    {
        private ApprovedPeer[] _approvals = Array.Empty<ApprovedPeer>();
        public Identity Identity { get; }
        public AudioPeerNetwork Network { get; }
        public AudioCoordinator Coordinator { get; }
        public Platform Platform { get; } = new();
        public Node(string id)
        {
            Identity = new(id);
            var inventory = new AudioDeviceInventory(id, "Synthetic audio computer", false, 1,
                new[] { new AudioOutputDescriptor("speakers", "Synthetic speakers", AudioOutputKind.Speakers, true, true, true),
                    new AudioOutputDescriptor("headphones", "Synthetic headphones", AudioOutputKind.Bluetooth, true, true, false) }, true, true);
            Network = new(id, Identity.Certificate, () => Volatile.Read(ref _approvals), () => inventory);
            Coordinator = new(inventory, Network, Platform); Network.Listen(0, IPAddress.Loopback);
        }
        public void Approve(ApprovedPeer[] approvals) => Volatile.Write(ref _approvals, approvals);
        public async ValueTask DisposeAsync()
        { Coordinator.Dispose(); Network.Dispose(); await Network.WhenStoppedAsync().WaitAsync(TimeSpan.FromSeconds(4)); Identity.Dispose(); }
    }
    private sealed class Platform : IAudioPlatform
    {
        public ConcurrentBag<Capture> Captures { get; } = new();
        public ConcurrentDictionary<string, Output> Outputs { get; } = new();
        public Capture? LiveCapture => Captures.FirstOrDefault(capture => capture.IsActive);
        public int LiveOutputs => Outputs.Values.Count(output => output.IsActive);
        public IAudioCapture CreateCapture() { var capture = new Capture(); Captures.Add(capture); return capture; }
        public IAudioOutput CreateOutput(string endpoint) { var output = new Output(); Outputs[endpoint] = output; return output; }
        public void Emit(float value, int count)
        { var capture = LiveCapture ?? throw new InvalidOperationException("Synthetic source has no consented capture."); for (var i = 0; i < count; i++) capture.Emit(value); }
        public float[] Pull(string endpoint) => Outputs[endpoint].Pull();
    }
    private sealed class Capture : IAudioCapture
    {
        private int _started; private int _disposed;
        public bool IsActive => Volatile.Read(ref _started) == 1 && Volatile.Read(ref _disposed) == 0;
        public event Action<float[]>? BlockAvailable;
        public event Action<Exception>? Faulted { add { } remove { } }
        public void Start() => Volatile.Write(ref _started, 1);
        // Also permits a deliberate late native callback after disposal.
        public void Emit(float value) => BlockAvailable?.Invoke(Enumerable.Repeat(value, AudioLimits.SamplesPerPacket).ToArray());
        public void Dispose() => Volatile.Write(ref _disposed, 1);
    }
    private sealed class Output : IAudioOutput
    {
        private int _started; private int _disposed; private Func<float[]?>? _provider;
        public bool IsActive => Volatile.Read(ref _started) == 1 && Volatile.Read(ref _disposed) == 0;
        public event Action<Exception>? Faulted { add { } remove { } }
        public void SetFrameProvider(Func<float[]?> provider) => _provider = provider;
        public void Start() => Volatile.Write(ref _started, 1);
        // Calling the old provider proves core consent clears PCM even before teardown.
        public float[] Pull() => _provider?.Invoke() ?? new float[AudioLimits.SamplesPerPacket];
        public void Dispose() => Volatile.Write(ref _disposed, 1);
    }
}
