using System.Diagnostics;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using MidiForwarder.Core;

namespace MidiForwarder.Ui;

/// <summary>Shared by the client/server and Windows/macOS settings windows.</summary>
internal sealed class MidiCapturePanel : UserControl
{
    private readonly MidiCapture _capture;
    private readonly string _role;
    private readonly string _folder;
    private readonly Button _toggle = new() { Name = "CaptureToggle", Content = "Start capture", HorizontalAlignment = HorizontalAlignment.Left };
    private readonly Button _mark = new() { Name = "CaptureAddMarker", Content = "Add marker" };
    private readonly Button _open = new() { Name = "CaptureOpenFolder", Content = "Open capture folder", HorizontalAlignment = HorizontalAlignment.Left };
    private readonly TextBlock _status = new() { Name = "CaptureStatus", TextWrapping = TextWrapping.Wrap, FontWeight = FontWeight.Medium };
    private readonly TextBlock _counts = new() { Name = "CaptureCounts", TextWrapping = TextWrapping.Wrap };
    private readonly TextBox _path = new() { Name = "CapturePath", IsReadOnly = true, TextWrapping = TextWrapping.Wrap, AcceptsReturn = true };
    private readonly TextBox _marker = new() { Name = "CaptureMarker", Watermark = "For example: renamed scene, before Save", MaxLength = 500 };
    private readonly TextBlock _feedback = new() { Name = "CaptureFeedback", TextWrapping = TextWrapping.Wrap };
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private bool _busy;

    public MidiCapturePanel(MidiCapture capture, string role, string? folder = null)
    {
        _capture = capture;
        _role = role;
        _folder = folder ?? DiagnosticLog.GetPath("Captures");
        _toggle.Classes.Add("accent");
        AutomationProperties.SetName(_path, "Capture file location");
        AutomationProperties.SetName(_marker, "Action marker");
        AutomationProperties.SetLiveSetting(_status, AutomationLiveSetting.Polite);
        AutomationProperties.SetLiveSetting(_feedback, AutomationLiveSetting.Polite);
        var actions = new StackPanel { Spacing = 8 };
        actions.Children.Add(Label("MIDI capture", 20));
        actions.Children.Add(Help("Record full MIDI messages in both directions, with timestamps. Capture starts now and stays off after restarting the app."));
        actions.Children.Add(_status);
        actions.Children.Add(_counts);
        actions.Children.Add(_toggle);
        actions.Children.Add(Label("Mark what you are doing"));
        actions.Children.Add(_marker);
        _mark.HorizontalAlignment = HorizontalAlignment.Left;
        actions.Children.Add(_mark);
        actions.Children.Add(_feedback);
        actions.Children.Add(Label("Capture file"));
        actions.Children.Add(_path);
        actions.Children.Add(_open);
        actions.Children.Add(Help("Stops at 100 MiB. Any lost capture records are reported; MIDI forwarding continues. Closing Settings does not stop capture. Files can contain preset names and settings; share them only when intended."));
        Content = new ScrollViewer
        {
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            Content = new Border { Padding = new Thickness(0, 20, 8, 20), Child = actions },
        };
        _toggle.Click += async (_, _) => await ToggleAsync().ConfigureAwait(true);
        _mark.Click += (_, _) => AddMarker();
        _marker.TextChanged += (_, _) => Refresh();
        _marker.KeyDown += (_, args) =>
        {
            if (args.Key == Avalonia.Input.Key.Enter) { AddMarker(); args.Handled = true; }
        };
        _open.Click += (_, _) => OpenFolder();
        _timer.Tick += (_, _) => Refresh();
        AttachedToVisualTree += (_, _) => _timer.Start();
        DetachedFromVisualTree += (_, _) => _timer.Stop();
        Refresh();
    }

    public void StopUpdates() => _timer.Stop();

    private async Task ToggleAsync()
    {
        if (_busy) { return; }
        _busy = true;
        _feedback.Text = null;
        try
        {
            if (_capture.GetSnapshot().IsRecording) { await _capture.StopAsync().ConfigureAwait(true); }
            else { _capture.Start(_folder, _role); }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _feedback.Text = $"Could not start capture: {error.Message}";
        }
        finally { _busy = false; Refresh(); }
    }

    private void AddMarker()
    {
        if (_capture.AddMarker(_marker.Text ?? string.Empty))
        {
            _feedback.Text = $"Marker added: {_marker.Text!.Trim()}";
            _marker.Text = string.Empty;
            _marker.Focus();
        }
        else { _feedback.Text = "Marker was not recorded. Check that capture is running and try again."; }
        Refresh();
    }

    private void OpenFolder()
    {
        try { Process.Start(new ProcessStartInfo(_folder) { UseShellExecute = true }); }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            _feedback.Text = $"Could not open the folder: {error.Message}";
        }
    }

    private void Refresh()
    {
        MidiCaptureSnapshot state = _capture.GetSnapshot();
        SetText(_status, state.Status);
        SetText(_counts, $"{state.Packets:N0} packets written · {state.FileBytes / 1048576d:N2} MiB · {state.LostRecords:N0} capture records lost");
        if (_path.Text != (state.Path ?? "Start capture to create a file.")) { _path.Text = state.Path ?? "Start capture to create a file."; }
        _toggle.Content = state.IsRecording ? "Stop capture" : "Start capture";
        _toggle.IsEnabled = !_busy && !state.IsFinishing;
        _marker.IsEnabled = state.IsRecording;
        _mark.IsEnabled = state.IsRecording && !string.IsNullOrWhiteSpace(_marker.Text);
        _open.IsEnabled = state.Path is not null;
    }

    private static void SetText(TextBlock block, string value) { if (block.Text != value) { block.Text = value; } }
    private static TextBlock Label(string text, double size = 14) => new() { Text = text, FontSize = size, FontWeight = FontWeight.Medium, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
    private static TextBlock Help(string text)
    {
        var block = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap };
        block.Classes.Add("secondary");
        return block;
    }
}
