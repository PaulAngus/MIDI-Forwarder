using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MidiForwarder.Client;
using MidiForwarder.Core;
using MidiForwarder.Server;

namespace MidiForwarder.Checks;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args is ["--sender-coexistence", string senderExe])
        {
            Task.Run(() => SenderCoexistenceCheck.RunAsync(senderExe)).WaitAsync(TimeSpan.FromSeconds(60)).GetAwaiter().GetResult();
            return 0;
        }
        if (args.Contains("--fm9-access", StringComparer.Ordinal))
        {
            Task.Run(() => Fm9AccessCheck.RunAsync(args.Contains("--ump", StringComparer.Ordinal),
                args.Contains("--with-server", StringComparer.Ordinal))).WaitAsync(TimeSpan.FromSeconds(45)).GetAwaiter().GetResult();
            return 0;
        }
        if (args.Contains("--shared-access", StringComparer.Ordinal))
        {
            Task.Run(() => VirtualGatewayCheck.CheckSharedAccessAsync(
                large: args.Contains("--large", StringComparer.Ordinal),
                forwarded: !args.Contains("--direct-only", StringComparer.Ordinal))).WaitAsync(TimeSpan.FromSeconds(45)).GetAwaiter().GetResult();
            return 0;
        }
        if (args.Contains("--virtual-gateway", StringComparer.Ordinal))
        { Task.Run(VirtualGatewayCheck.RunAsync).WaitAsync(TimeSpan.FromSeconds(45)).GetAwaiter().GetResult(); return 0; }
        string directory = Path.GetFullPath(args.FirstOrDefault() ?? "artifacts/gateway-preview");
        Directory.CreateDirectory(directory);
        AppBuilder.Configure<PreviewApp>().UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).SetupWithoutStarting();
        using var serverService = new AsyncServiceScope<ServerRelayService>(new ServerRelayService());
        using var clientService = new AsyncServiceScope<ClientRelayService>(new ClientRelayService());
        var server = new ServerWindow(serverService.Value, preview: true);
        var client = new ClientWindow(clientService.Value, preview: true);
        server.SetPreviewState(new GatewayState([
            new GatewayConnection("local-a", "MIDI Gateway 1", false),
            new GatewayConnection("local-b", "MIDI Gateway 2", false),
            new GatewayConnection("remote", "Remote: MIDI Forwarder (192.0.2.10:51800)", true)],
            "Direct device access: Another MIDI app (PID 123). MIDI forwarding remains shared."));
        client.SetPreviewState("MIDI Forwarder: Connected\nMIDI Editor: Connected");
        Capture(server, "server", directory);
        Capture(client, "client", directory);
        server.SetPreviewState(new GatewayState([], "Direct device access has not been checked."), running: false);
        client.SetPreviewState("Stopped", running: false);
        CaptureConfiguration(server, "server", directory);
        CaptureConfiguration(client, "client", directory);
        server.AllowClose(); client.AllowClose(); server.Close(); client.Close();
        Console.WriteLine($"Rendered light/dark, normal/narrow previews in {directory}");
        return 0;
    }

    private static void Capture(Window window, string name, string directory)
    {
        window.Show();
        window.Activate();
        foreach (ThemeVariant theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
        foreach (bool narrow in new[] { false, true })
        {
            window.RequestedThemeVariant = theme;
            window.Width = narrow ? 480 : name == "server" ? 760 : 700;
            window.Height = narrow ? 600 : name == "server" ? 900 : 760;
            Dispatcher.UIThread.RunJobs();
            using (var animationTime = new CancellationTokenSource(TimeSpan.FromMilliseconds(300)))
            { Dispatcher.UIThread.MainLoop(animationTime.Token); }
            using var frame = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("No rendered frame.");
            frame.Save(Path.Combine(directory, $"{name}-{theme}-{(narrow ? "narrow" : "normal")}.png"));
        }
        var focused = new HashSet<string>(StringComparer.Ordinal);
        window.GetVisualDescendants().OfType<Button>().First(button => button.IsEffectivelyEnabled && button.IsVisible).Focus();
        for (int i = 0; i < 20; i++)
        {
            window.KeyPress(Key.Tab, RawInputModifiers.None, PhysicalKey.Tab, "\t");
            window.KeyRelease(Key.Tab, RawInputModifiers.None, PhysicalKey.Tab, "\t");
            if (window.FocusManager?.GetFocusedElement() is Control control)
            { focused.Add(AutomationProperties.GetName(control) ?? (control as Button)?.Content?.ToString() ?? control.GetType().Name); }
        }
        Console.WriteLine($"{name} keyboard targets: {string.Join(", ", focused)}");
        foreach (ScrollViewer scroll in window.GetVisualDescendants().OfType<ScrollViewer>().Where(scroll => scroll.Extent.Height > scroll.Viewport.Height))
        { scroll.Offset = new Vector(0, scroll.Extent.Height); }
        Dispatcher.UIThread.RunJobs();
        using var bottom = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("No rendered frame.");
        bottom.Save(Path.Combine(directory, $"{name}-Dark-narrow-bottom.png"));
    }

    private static void CaptureConfiguration(Window window, string name, string directory)
    {
        foreach (ThemeVariant theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
        foreach (bool narrow in new[] { false, true })
        {
            window.RequestedThemeVariant = theme; window.Width = narrow ? 480 : name == "server" ? 760 : 700;
            window.Height = narrow ? 600 : name == "server" ? 900 : 760;
            foreach (ScrollViewer scroll in window.GetVisualDescendants().OfType<ScrollViewer>()) { scroll.Offset = default; }
            Dispatcher.UIThread.RunJobs();
            using (var animationTime = new CancellationTokenSource(TimeSpan.FromMilliseconds(300)))
            { Dispatcher.UIThread.MainLoop(animationTime.Token); }
            using var frame = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("No configuration frame.");
            frame.Save(Path.Combine(directory, $"{name}-{theme}-{(narrow ? "narrow" : "normal")}-settings.png"));
        }
    }

    private sealed class AsyncServiceScope<T>(T value) : IDisposable where T : IAsyncDisposable
    { public T Value { get; } = value; public void Dispose() => Value.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
}

public sealed class PreviewApp : Application
{ public override void Initialize() => Styles.Add(new FluentTheme()); }
