using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace MidiForwarder.Client.Tests;

public sealed class ClientTakeoverUiTests
{
    [AvaloniaTheory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ConfirmationExplainsDisconnectionAndReturnsTheSelectedDecision(bool confirm, bool dark)
    {
        await using var service = new ClientRelayService();
        var window = new ClientWindow(service, () => new ClientSettings { ServerUri = "tcp://test-server:5180" }, _ => { })
        {
            RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light,
        };
        try
        {
            window.ShowMainWindow();
            Task<bool> result = window.ShowTakeoverDialogAsync(CancellationToken.None);
            Window dialog = window.OwnedWindows.Single(w => w.Name == "TakeoverDialog");
            dialog.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            Assert.Contains(dialog.GetVisualDescendants().OfType<TextBlock>(), text => text.Text?.Contains("disconnect that client", StringComparison.Ordinal) == true);
            Button cancel = dialog.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "CancelTakeoverButton");
            Button takeOver = dialog.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "ConfirmTakeoverButton");
            Assert.True(cancel.IsEffectivelyVisible);
            Assert.True(takeOver.IsEffectivelyVisible);
            Assert.InRange(takeOver.TranslatePoint(new Avalonia.Point(0, takeOver.Bounds.Height), dialog)!.Value.Y, 0, dialog.ClientSize.Height);
            Assert.False(result.IsCompleted);
            string? snapshots = Environment.GetEnvironmentVariable("MIDI_FORWARDER_UI_SNAPSHOTS");
            if (!string.IsNullOrEmpty(snapshots))
            {
                Directory.CreateDirectory(snapshots);
                await Task.Delay(120);
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                using var frame = dialog.CaptureRenderedFrame();
                frame?.Save(Path.Combine(snapshots, $"takeover-{dark}-{confirm}.png"));
            }
            (confirm ? takeOver : cancel).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(confirm, await result);
            Assert.Empty(window.OwnedWindows);
        }
        finally { window.AllowClose(); window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WindowCloseOrCancellationDeclinesTakeover(bool cancelToken)
    {
        using var cancellation = new CancellationTokenSource();
        await using var service = new ClientRelayService();
        var window = new ClientWindow(service, () => new ClientSettings(), _ => { });
        try
        {
            window.ShowMainWindow();
            Task<bool> result = window.ShowTakeoverDialogAsync(cancellation.Token);
            Window dialog = window.OwnedWindows.Single(w => w.Name == "TakeoverDialog");
            if (cancelToken)
            {
                await cancellation.CancelAsync();
                Dispatcher.UIThread.RunJobs();
            }
            else { dialog.Close(); }
            Assert.False(await result);
        }
        finally { window.AllowClose(); window.Close(); }
    }
}
