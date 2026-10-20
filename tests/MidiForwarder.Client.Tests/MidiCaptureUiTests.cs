using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MidiForwarder.Ui;

namespace MidiForwarder.Client.Tests;

public sealed class MidiCaptureUiTests
{
    [AvaloniaTheory]
    [InlineData(false, 560, 640)]
    [InlineData(true, 560, 640)]
    [InlineData(false, 440, 440)]
    [InlineData(true, 440, 440)]
    public async Task CanStartMarkAndStopWithoutReconnectingAndCaptureSurvivesClosingSettings(bool dark, double width, double height)
    {
        string folder = Path.Combine(Path.GetTempPath(), "midi-capture-ui", Guid.NewGuid().ToString("N"));
        await using var service = new ClientRelayService();
        var window = new ClientWindow(service) { RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light };
        try
        {
            var panel = new MidiCapturePanel(service.Capture, "client", folder);
            window.Control<ContentControl>("CaptureHost")!.Content = panel;
            window.ShowConfiguration(3);
            var settings = window.ConfigurationWindow;
            settings.Width = width; settings.Height = height;
            settings.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            T Find<T>(string name) where T : Control => panel.GetVisualDescendants().OfType<T>().Single(c => c.Name == name);
            var toggle = Find<Button>("CaptureToggle");
            Assert.Equal("Start capture", toggle.Content);
            toggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.True(service.Capture.GetSnapshot().IsRecording);
            var marker = Find<TextBox>("CaptureMarker");
            marker.Text = "Renamed scene: before Save";
            marker.Focus();
            marker.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
            Assert.Equal(string.Empty, marker.Text);
            Assert.Contains("Marker added", Find<TextBlock>("CaptureFeedback").Text);
            string? snapshots = Environment.GetEnvironmentVariable("MIDI_FORWARDER_UI_SNAPSHOTS");
            if (!string.IsNullOrEmpty(snapshots))
            {
                Directory.CreateDirectory(snapshots);
                marker.Text = "Swapped scenes 1 and 2";
                await Task.Delay(120);
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                using var frame = settings.CaptureRenderedFrame();
                frame?.Save(Path.Combine(snapshots, $"client-capture-{dark}-{width}-{height}-recording.png"));
                var scroll = panel.GetVisualDescendants().OfType<ScrollViewer>().First();
                scroll.Offset = new Avalonia.Vector(0, scroll.Extent.Height);
                await Task.Delay(120);
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                using var bottom = settings.CaptureRenderedFrame();
                bottom?.Save(Path.Combine(snapshots, $"client-capture-{dark}-{width}-{height}-bottom.png"));
            }
            settings.Close();
            Assert.True(service.Capture.GetSnapshot().IsRecording);
            await Task.Delay(150);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("Settings (capture on)", window.Control<Button>("SettingsButton")!.Content);
            window.Control<Button>("SettingsButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(3, window.Control<TabControl>("SettingsTabs")!.SelectedIndex);
            toggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await service.Capture.StopAsync();
            Assert.False(service.Capture.GetSnapshot().IsRecording);
            Assert.Equal(0, service.Capture.GetSnapshot().LostRecords);
            string text = await File.ReadAllTextAsync(service.Capture.GetSnapshot().Path!);
            Assert.Contains("Renamed scene: before Save", text);
            Assert.Contains("capture-end", text);
        }
        finally { window.AllowClose(); window.Close(); }
    }
}
