using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Avalonia.Threading;
using MidiForwarder.Core;

namespace MidiForwarder.Server.Tests;

public sealed class ServerWindowTests
{
    [AvaloniaTheory]
    [InlineData(false, 520, 640)]
    [InlineData(true, 520, 640)]
    [InlineData(false, 400, 640)]
    [InlineData(true, 400, 640)]
    [InlineData(false, 400, 440)]
    [InlineData(true, 400, 440)]
    public async Task SeparateSettingsPagesFitWithoutResizingTheMainWindow(bool dark, double width, double height)
    {
        await using var service = new ServerRelayService();
        var window = new ServerWindow(service)
        {
            Width = width,
            RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light,
        };
        try
        {
            window.ShowMainWindow();
            await window.MidiDiscoveryTask;
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            double mainHeight = window.Bounds.Height;
            Assert.InRange(mainHeight, 350, 580);
            Assert.True(window.Control<Button>("SettingsButton")!.IsEffectivelyVisible);
            Assert.Null(window.FindControl<TabControl>("SettingsTabs"));
            var configuration = window.ConfigurationWindow;
            configuration.Width = width < 440 ? 440 : 560;
            configuration.Height = height;
            string? output = Environment.GetEnvironmentVariable("MIDI_FORWARDER_UI_SNAPSHOTS");
            if (!string.IsNullOrWhiteSpace(output))
            {
                Directory.CreateDirectory(output);
                await Task.Delay(100);
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                using var mainFrame = window.CaptureRenderedFrame();
                mainFrame?.Save(Path.Combine(output, $"server-{(dark ? "dark" : "light")}-{width}-{height}-main.png"));
            }

            for (int page = 0; page < 3; page++)
            {
                window.ShowConfiguration(page);
                configuration.UpdateLayout();
                Dispatcher.UIThread.RunJobs();
                Assert.Same(configuration, window.ConfigurationWindow);
                Assert.True(configuration.IsVisible);
                Assert.Equal(page, window.Control<TabControl>("SettingsTabs")!.SelectedIndex);
                Assert.Equal(mainHeight, window.Bounds.Height);
                Assert.Equal(height, configuration.Height);
                Assert.True(window.Control<Button>("CloseSettingsButton")!.IsEffectivelyVisible);
                var close = window.Control<Button>("CloseSettingsButton")!;
                Assert.InRange(close.TranslatePoint(new Point(0, close.Bounds.Height), configuration)!.Value.Y,
                    0, configuration.ClientSize.Height);
                var connection = window.Control<Button>("SettingsConnectionButton")!;
                Assert.True(connection.IsEffectivelyVisible);
                Assert.InRange(connection.Bounds.Width, 80, configuration.ClientSize.Width);
                Assert.InRange(connection.TranslatePoint(new Point(0, connection.Bounds.Height), configuration)!.Value.Y,
                    0, configuration.ClientSize.Height);
                Assert.Equal(page == 0, window.Control<Button>("SaveConnectionButton")!.IsVisible);
                if (page == 0) { Assert.NotEqual('\0', window.Control<TextBox>("Token")!.PasswordChar); }
                if (page == 2)
                {
                    var log = window.Control<TextBox>("LogText")!;
                    var backend = window.Control<TextBlock>("BackendDescription")!;
                    Assert.InRange(log.Bounds.Height, 24, configuration.ClientSize.Height);
                    Assert.InRange(backend.TranslatePoint(new Point(0, backend.Bounds.Height), configuration)!.Value.Y,
                        0, close.TranslatePoint(new Point(0, 0), configuration)!.Value.Y);
                }
                if (!string.IsNullOrWhiteSpace(output))
                {
                    await Task.Delay(100);
                    AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                    using var frame = configuration.CaptureRenderedFrame();
                    frame?.Save(Path.Combine(output, $"server-{(dark ? "dark" : "light")}-{width}-{height}-settings-{page}.png"));
                }
            }

            configuration.Close();
            Assert.False(configuration.IsVisible);
            Assert.True(window.IsVisible);
            window.ShowConfiguration();
            Assert.Same(configuration, window.ConfigurationWindow);
            Assert.True(configuration.IsVisible);
            window.Control<Button>("HideButton")!.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Assert.False(configuration.IsVisible);
            Assert.False(window.IsVisible);
            window.ShowMainWindow();
            Assert.True(window.IsVisible);
            Assert.False(configuration.IsVisible);
        }
        finally
        {
            window.AllowClose();
            window.Close();
        }
    }
    [AvaloniaFact]
    public async Task ActivityLightsDistinguishDirectionsAndExpireWithoutLosingCounts()
    {
        await using var service = new ServerRelayService();
        var window = new ServerWindow(service);
        try
        {
            service.Traffic.RecordMidiToNetwork(3);
            MidiTrafficSnapshot active = service.Traffic.GetSnapshot();
            window.UpdateTraffic(active, connected: true);
            Assert.Contains("active", window.Control<Border>("ToClientLamp")!.Classes);
            Assert.DoesNotContain("active", window.Control<Border>("ToDeviceLamp")!.Classes);
            Assert.Equal("Idle", window.Control<TextBlock>("ToDeviceState")!.Text);
            Assert.Equal("1 message", window.Control<TextBlock>("ToClientMessages")!.Text);

            long oldTimestamp = Stopwatch.GetTimestamp() - Stopwatch.Frequency;
            window.UpdateTraffic(active with { MidiToNetwork = active.MidiToNetwork with { LastActivityTimestamp = oldTimestamp } }, connected: true);
            Assert.DoesNotContain("active", window.Control<Border>("ToClientLamp")!.Classes);
            Assert.Equal("Idle", window.Control<TextBlock>("ToClientState")!.Text);
            Assert.Equal("1 message", window.Control<TextBlock>("ToClientMessages")!.Text);

            window.UpdateTraffic(active, connected: false);
            Assert.DoesNotContain("active", window.Control<Border>("ToClientLamp")!.Classes);
            Assert.Equal("Last connection", window.Control<TextBlock>("TrafficSession")!.Text);
        }
        finally
        {
            window.AllowClose();
            window.Close();
        }
    }
}
