using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace MidiForwarder;

internal enum TrayConnectionState
{
    Inactive,
    Connecting,
    Active,
}

internal readonly record struct TrayStatus(TrayConnectionState State, string Description);

/// <summary>Updates independently of window visibility and caches the three status icons.</summary>
internal sealed class TrayStatusIndicator : IDisposable
{
    private readonly TrayIcon _trayIcon;
    private readonly NativeMenuItem _stopCommand;
    private readonly string _title;
    private readonly Func<TrayStatus> _readStatus;
    private readonly Dictionary<TrayConnectionState, WindowIcon> _icons = new();
    private TrayConnectionState? _displayedState;
    private bool _disposed;

    public TrayStatusIndicator(TrayIcon trayIcon, NativeMenuItem stopCommand, string title, Func<TrayStatus> readStatus)
    {
        _trayIcon = trayIcon;
        _stopCommand = stopCommand;
        _title = title;
        _readStatus = readStatus;
        Update();
    }

    public void OnStatusChanged(object? sender, string status)
    {
        if (Dispatcher.UIThread.CheckAccess()) { Update(); }
        else { Dispatcher.UIThread.Post(Update); }
    }

    public void Dispose() => _disposed = true;

    private void Update()
    {
        if (_disposed) { return; }

        // Read the latest state on the UI thread; queued events may describe an older connection.
        TrayStatus status = _readStatus();
        if (_displayedState != status.State)
        {
            if (!_icons.TryGetValue(status.State, out WindowIcon? icon))
            {
                icon = CreateIcon(status.State);
                _icons.Add(status.State, icon);
            }

            _trayIcon.Icon = icon;
            _displayedState = status.State;
        }

        _trayIcon.ToolTipText = $"{_title}: {status.Description}";
        _stopCommand.IsEnabled = status.State != TrayConnectionState.Inactive;
    }

    private static WindowIcon CreateIcon(TrayConnectionState state)
    {
        using var bitmap = RenderIcon(state);
        return new WindowIcon(bitmap);
    }

    internal static RenderTargetBitmap RenderIcon(TrayConnectionState state)
    {
        var bitmap = new RenderTargetBitmap(new PixelSize(128, 128), new Vector(96, 96));
        using (DrawingContext drawing = bitmap.CreateDrawingContext())
        {
            drawing.DrawImage(BrandAssets.Logo, new Rect(0, 0, 128, 128));

            // A large corner badge stays legible when Windows displays the icon at 16 px.
            var center = new Point(96, 96);
            drawing.DrawEllipse(Brushes.White, null, center, 31, 31);
            drawing.DrawEllipse(new SolidColorBrush(Color.Parse("#142235")), null, center, 29, 29);
            var fill = new SolidColorBrush(Color.Parse(state switch
            {
                TrayConnectionState.Active => "#16A34A",
                TrayConnectionState.Connecting => "#FBBF24",
                _ => "#64748B",
            }));
            drawing.DrawEllipse(fill, null, center, 26, 26);

            if (state == TrayConnectionState.Active)
            {
                var pen = new Pen(Brushes.White, 8, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
                drawing.DrawLine(pen, new Point(82, 96), new Point(92, 106));
                drawing.DrawLine(pen, new Point(92, 106), new Point(111, 86));
            }
            else if (state == TrayConnectionState.Connecting)
            {
                var pen = new Pen(new SolidColorBrush(Color.Parse("#142235")), 7, lineCap: PenLineCap.Round);
                drawing.DrawLine(pen, new Point(96, 81), center);
                drawing.DrawLine(pen, center, new Point(108, 102));
            }
            else
            {
                drawing.DrawLine(new Pen(Brushes.White, 8, lineCap: PenLineCap.Round),
                    new Point(83, 96), new Point(109, 96));
            }
        }

        return bitmap;
    }
}
