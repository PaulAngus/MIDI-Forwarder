using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using MidiForwarder.Client;
using MidiForwarder.Server;
using MightBeUseful.Midi.CoreMidi;

namespace MidiForwarder.MacWorkflow.Tests;

// These are development-VM native tests. They create temporary CoreMIDI endpoints.
// Never include this project in physical/offline profiles. No hardware is selected.
[Trait("Category", "Native")]
public sealed class ClientServerWorkflowTests
{
    [AppleSessionFact]
    public async Task DefaultAppleNetworkClientConnectsAndTransfersInBothDirections()
    {
        await using var setup = new Connection(useAppleNetworkMidi: true);
        await setup.Server.StartAsync(setup.ServerSettings);
        await setup.StartClientAsync();
        await setup.WaitForServerAsync(s => s == "Client connected; MIDI endpoint open");
        await setup.RoundTripAsync([0xc0, 42]);
        await setup.RoundTripAsync(SysEx(3082));
    }

    [MacTheory]
    [InlineData("program-change")]
    [InlineData("control-change")]
    [InlineData("note-on-off")]
    [InlineData("system-common")]
    [InlineData("realtime")]
    [InlineData("sysex")]
    [InlineData("large-sysex")]
    public async Task ApplicationMessagesReachTheDeviceAndRepliesReachTheApplication(string kind)
    {
        await using var setup = await Connection.StartAsync();
        byte[][] messages = kind switch
        {
            "program-change" => [[0xc0, 0], [0xcf, 127]],
            "control-change" => [[0xb0, 0, 127], [0xbf, 127, 0]],
            "note-on-off" => [[0x90, 60, 100], [0x80, 60, 0]],
            "system-common" => [[0xf1, 12], [0xf2, 1, 2], [0xf6]],
            "realtime" => [[0xf8], [0xfa], [0xfc]],
            "sysex" => [SysEx(3082)],
            "large-sysex" => [SysEx(70000)],
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
        foreach (byte[] message in messages) await setup.RoundTripAsync(message);
        Assert.Equal(messages.Length, setup.Client.Traffic.GetSnapshot().MidiToNetwork.Messages);
        Assert.Equal(messages.Length, setup.Server.Traffic.GetSnapshot().NetworkToMidi.Messages);
        Assert.Equal(messages.Length, setup.Server.Traffic.GetSnapshot().MidiToNetwork.Messages);
        Assert.Equal(messages.Length, setup.Client.Traffic.GetSnapshot().NetworkToMidi.Messages);
    }

    [MacFact]
    public async Task ServerRestartReconnectsAndContinuesBothDirections()
    {
        await using var setup = await Connection.StartAsync();
        await setup.RoundTripAsync([0xc0, 11]);
        await setup.Server.StopAsync();
        await setup.WaitForClientAsync(s => s.StartsWith("Reconnecting", StringComparison.Ordinal));
        Assert.False(setup.Client.IsConnected);
        await setup.Server.StartAsync(setup.ServerSettings);
        await setup.WaitForServerAsync(s => s == "Client connected; MIDI endpoint open");
        await setup.RoundTripAsync([0xc0, 12]);
        Assert.Null(setup.Client.LastConnectionError);
        Assert.Null(setup.Server.LastConnectionError);
    }

    [MacFact]
    public async Task UnavailableServerCanStartLaterWithoutRestartingTheClient()
    {
        await using var setup = new Connection();
        await setup.StartClientAsync();
        await setup.WaitForClientAsync(s => s.StartsWith("Reconnecting", StringComparison.Ordinal));
        Assert.False(setup.Client.IsConnected);
        Assert.NotNull(setup.Client.LastConnectionError);
        await setup.Server.StartAsync(setup.ServerSettings);
        await setup.WaitForServerAsync(s => s == "Client connected; MIDI endpoint open");
        await setup.RoundTripAsync([0xb0, 7, 99]);
    }

    [MacFact]
    public async Task WrongTokenIsRejectedAndACorrectedConnectionWorks()
    {
        await using var setup = new Connection();
        await setup.Server.StartAsync(setup.ServerSettings);
        await setup.StartClientAsync(setup.ClientSettings with { Token = "wrong-token" });
        await setup.WaitForClientAsync(s => s.StartsWith("Reconnecting", StringComparison.Ordinal));
        Assert.Null(setup.Server.ConnectedClient);
        Assert.Contains("authentication failed", setup.Server.LastConnectionError);
        Assert.Equal(0, setup.Server.Traffic.GetSnapshot().NetworkToMidi.Messages);
        await setup.Client.StopAsync();
        await setup.Application!.DisposeAsync();
        setup.Application = null;
        await setup.StartClientAsync();
        await setup.WaitForServerAsync(s => s == "Client connected; MIDI endpoint open");
        await setup.RoundTripAsync([0xc0, 7]);
    }

    [MacFact]
    public async Task DisconnectStopsRetriesAndReleasesTheApplicationPorts()
    {
        await using var setup = new Connection();
        await setup.StartClientAsync();
        await setup.WaitForClientAsync(s => s.StartsWith("Reconnecting", StringComparison.Ordinal));
        await setup.Client.StopAsync();
        Assert.False(setup.Client.IsRunning);
        Assert.False(setup.Client.IsConnected);
        Assert.Equal("Stopped", setup.Client.ConnectionStatus);
        Assert.DoesNotContain(setup.ClientSettings.InterfaceName, CoreMidiPortCatalog.GetNames());
        await setup.Application!.DisposeAsync();
        setup.Application = null;
        await setup.Server.StartAsync(setup.ServerSettings);
        Assert.Null(setup.Server.ConnectedClient);
        // A deliberate Connect after Disconnect must open a fresh connection.
        await setup.StartClientAsync();
        await setup.WaitForServerAsync(s => s == "Client connected; MIDI endpoint open");
        await setup.RoundTripAsync([0xc0, 3]);
    }

    [MacFact]
    public async Task MissingAppleSessionExplainsThePrerequisiteAndLeavesTheClientReusable()
    {
        await using var setup = new Connection();
        var settings = setup.ClientSettings with
        {
            UseAppleNetworkMidi = true,
            NetworkMidiEndpoint = "Absent workflow session " + Guid.NewGuid().ToString("N")
        };
        IOException error = await Assert.ThrowsAsync<IOException>(() => setup.Client.StartAsync(settings));
        Assert.Contains("Audio MIDI Setup", error.Message);
        Assert.False(setup.Client.IsRunning);
        Assert.False(setup.Client.IsConnected);
        // The failed default connection path must not poison a later valid start.
        await setup.Server.StartAsync(setup.ServerSettings);
        await setup.StartClientAsync();
        await setup.WaitForServerAsync(s => s == "Client connected; MIDI endpoint open");
        await setup.RoundTripAsync([0xc0, 4]);
    }

    [MacFact]
    public async Task MissingServerMidiSelectionFailsBeforeListening()
    {
        await using var setup = new Connection();
        var settings = setup.ServerSettings with { MidiEndpoint = "Absent " + Guid.NewGuid().ToString("N") };
        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() => setup.Server.StartAsync(settings));
        Assert.Contains("Refresh MIDI ports", error.Message);
        Assert.False(setup.Server.IsRunning);
        await setup.Server.StartAsync(setup.ServerSettings);
        await setup.StartClientAsync();
        await setup.WaitForServerAsync(s => s == "Client connected; MIDI endpoint open");
        await setup.RoundTripAsync([0xc0, 5]);
    }

    private static byte[] SysEx(int size)
    {
        byte[] bytes = new byte[size];
        bytes[0] = 0xf0; bytes[1] = 0x7d; bytes[^1] = 0xf7;
        for (int i = 2; i < size - 1; i++) bytes[i] = (byte)(i % 128);
        return bytes;
    }

    private sealed class Connection : IAsyncDisposable
    {
        private readonly CancellationTokenSource deadline = new(TimeSpan.FromSeconds(25));
        private readonly Channel<string> clientStates = Channel.CreateUnbounded<string>();
        private readonly Channel<string> serverStates = Channel.CreateUnbounded<string>();
        private readonly CoreMidiDuplexPort device;
        private readonly string deviceName = "WF Device " + Guid.NewGuid().ToString("N")[..12];
        public ClientRelayService Client { get; } = new();
        public ServerRelayService Server { get; } = new();
        public IMidiDuplexPort? Application { get; set; }
        public ClientSettings ClientSettings { get; }
        public ServerSettings ServerSettings { get; }

        public Connection(bool useAppleNetworkMidi = false)
        {
            using var reservation = new TcpListener(IPAddress.Loopback, 0);
            reservation.Start();
            int port = ((IPEndPoint)reservation.LocalEndpoint).Port;
            ClientSettings = new()
            {
                InterfaceName = "WF App " + Guid.NewGuid().ToString("N")[..12],
                UseAppleNetworkMidi = useAppleNetworkMidi, ServerUri = $"tcp://127.0.0.1:{port}", Token = "workflow-only"
            };
            ServerSettings = new() { MidiEndpoint = deviceName, ListenUrl = ClientSettings.ServerUri, Token = ClientSettings.Token };
            device = CoreMidiDuplexPort.OpenVirtual(deviceName);
            Client.StatusChanged += (_, status) => clientStates.Writer.TryWrite(status);
            Server.StatusChanged += (_, status) => serverStates.Writer.TryWrite(status);
        }

        public static async Task<Connection> StartAsync()
        {
            var setup = new Connection();
            try
            {
                await setup.Server.StartAsync(setup.ServerSettings);
                await setup.StartClientAsync();
                await setup.WaitForServerAsync(s => s == "Client connected; MIDI endpoint open");
                return setup;
            }
            catch { await setup.DisposeAsync(); throw; }
        }

        public async Task StartClientAsync(ClientSettings? settings = null)
        {
            await Client.StartAsync(settings ?? ClientSettings);
            Application = CoreMidiDuplexPort.Open(ClientSettings.UseAppleNetworkMidi
                ? ClientSettings.NetworkMidiEndpoint : ClientSettings.InterfaceName);
        }

        public async Task RoundTripAsync(byte[] bytes)
        {
            await using var deviceReader = device.ReadAllAsync(deadline.Token).GetAsyncEnumerator();
            await using var appReader = Application!.ReadAllAsync(deadline.Token).GetAsyncEnumerator();
            await Application.SendAsync(new(MidiPacketFormat.Midi1, bytes), deadline.Token);
            Assert.True(await deviceReader.MoveNextAsync());
            Assert.Equal(bytes, deviceReader.Current.Data.ToArray());
            await device.SendAsync(new(MidiPacketFormat.Midi1, bytes), deadline.Token);
            Assert.True(await appReader.MoveNextAsync());
            Assert.Equal(bytes, appReader.Current.Data.ToArray());
        }

        public Task WaitForClientAsync(Func<string, bool> match) => WaitAsync(clientStates, match);
        public Task WaitForServerAsync(Func<string, bool> match) => WaitAsync(serverStates, match);
        private async Task WaitAsync(Channel<string> states, Func<string, bool> match)
        {
            while (!match(await states.Reader.ReadAsync(deadline.Token))) { }
        }

        public async ValueTask DisposeAsync()
        {
            await Client.DisposeAsync();
            await Server.DisposeAsync();
            if (Application is not null) await Application.DisposeAsync();
            await device.DisposeAsync();
            Assert.DoesNotContain(ClientSettings.InterfaceName, CoreMidiPortCatalog.GetNames());
            Assert.DoesNotContain(deviceName, CoreMidiPortCatalog.GetNames());
            deadline.Dispose();
        }
    }
}

internal sealed class MacFactAttribute : FactAttribute
{
    public MacFactAttribute() { if (!OperatingSystem.IsMacOS()) Skip = "Development macOS VM native workflow only."; }
}
internal sealed class MacTheoryAttribute : TheoryAttribute
{
    public MacTheoryAttribute() { if (!OperatingSystem.IsMacOS()) Skip = "Development macOS VM native workflow only."; }
}
internal sealed class AppleSessionFactAttribute : FactAttribute
{
    public AppleSessionFactAttribute()
    {
        if (!OperatingSystem.IsMacOS()) Skip = "Development macOS VM native workflow only.";
        else if (!CoreMidiPortCatalog.GetNames().Contains(new ClientSettings().NetworkMidiEndpoint, StringComparer.Ordinal))
            Skip = "Default Apple Network MIDI workflow is unqualified: the existing enabled 'Network RTP Session 1' session is absent. No session is created by this test.";
    }
}
