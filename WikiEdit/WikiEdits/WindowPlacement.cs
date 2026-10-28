using System.IO;
using System.Text.Json;
using System.Windows;

namespace WikiEdits;

/// <summary>
/// Window size, position and state, saved on exit and restored on the next start.
/// Stored as a JSON file in the user's application data folder, next to the query history.
/// </summary>
public sealed class WindowPlacement
{
    public double Left { get; set; }
    public double Top { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public WindowState State { get; set; }

    private static string FilePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WikiEdits", "window.json");

    /// <summary>Reads the saved placement. Returns null when nothing was saved or the file is unreadable.</summary>
    public static WindowPlacement? Load()
    {
        try
        {
            if (!File.Exists(FilePath))
                return null;
            return JsonSerializer.Deserialize<WindowPlacement>(File.ReadAllText(FilePath));
        }
        catch (Exception)
        {
            return null; // A corrupt file is not worth failing over; the window just opens with defaults.
        }
    }

    /// <summary>Captures the window's placement. When maximized or minimized, the normal-state bounds are stored.</summary>
    public static WindowPlacement Capture(Window window)
    {
        var bounds = window.WindowState == WindowState.Normal
            ? new Rect(window.Left, window.Top, window.Width, window.Height)
            : window.RestoreBounds;

        return new WindowPlacement
        {
            Left = bounds.Left,
            Top = bounds.Top,
            Width = bounds.Width,
            Height = bounds.Height,
            State = window.WindowState,
        };
    }

    /// <summary>
    /// Applies the placement to a window that has not been shown yet.
    /// The position is used only if the window would still be visible on the current screens,
    /// so a layout saved on a disconnected monitor does not open the window off screen.
    /// </summary>
    public void ApplyTo(Window window)
    {
        if (Width >= window.MinWidth && Height >= window.MinHeight)
        {
            window.Width = Width;
            window.Height = Height;
        }

        if (IsOnScreen(Left, Top, Width, Height))
        {
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left = Left;
            window.Top = Top;
        }

        window.WindowState = State;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception)
        {
            // Not being able to remember the window placement must not block closing the program.
        }
    }

    private static bool IsOnScreen(double left, double top, double width, double height)
    {
        if (double.IsNaN(left) || double.IsNaN(top) || width <= 0 || height <= 0)
            return false;

        // Require at least a 100 x 100 corner of the window inside the virtual screen (all monitors combined)
        var screen = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
                              SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        var window = new Rect(left, top, width, height);
        var visible = Rect.Intersect(screen, window);
        return !visible.IsEmpty && visible.Width >= 100 && visible.Height >= 100;
    }
}
