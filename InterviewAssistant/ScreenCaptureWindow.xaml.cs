using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Forms;

namespace InterviewAssistant;

public partial class ScreenCaptureWindow : Window
{
    private readonly BitmapSource _screenshot;
    private readonly System.Drawing.Rectangle _screenBounds;
    private System.Windows.Point? _selectionStart;
    public byte[]? SelectedPng { get; private set; }

    public ScreenCaptureWindow(Screen screen)
    {
        _screenBounds = screen.Bounds;
        _screenshot = CaptureScreen(_screenBounds);
        InitializeComponent();
        ScreenshotImage.Source = _screenshot;
        SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(this).Handle;
            SetWindowPos(handle, new IntPtr(-1), _screenBounds.Left, _screenBounds.Top,
                _screenBounds.Width, _screenBounds.Height, 0x0040);
        };
    }

    private static BitmapSource CaptureScreen(System.Drawing.Rectangle bounds)
    {
        using var bitmap = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap))
            graphics.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, bitmap.Size);
        using var buffer = new MemoryStream();
        bitmap.Save(buffer, ImageFormat.Png);
        buffer.Position = 0;
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = buffer;
        image.EndInit();
        image.Freeze();
        return image;
    }

    private void SelectionLayer_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _selectionStart = e.GetPosition(SelectionLayer);
        SelectionRectangle.Visibility = Visibility.Visible;
        SelectionRectangle.Width = SelectionRectangle.Height = 0;
        Canvas.SetLeft(SelectionRectangle, _selectionStart.Value.X);
        Canvas.SetTop(SelectionRectangle, _selectionStart.Value.Y);
        SelectionLayer.CaptureMouse();
        e.Handled = true;
    }

    private void SelectionLayer_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_selectionStart is not { } start || e.LeftButton != MouseButtonState.Pressed) return;
        var current = e.GetPosition(SelectionLayer);
        Canvas.SetLeft(SelectionRectangle, Math.Min(start.X, current.X));
        Canvas.SetTop(SelectionRectangle, Math.Min(start.Y, current.Y));
        SelectionRectangle.Width = Math.Abs(current.X - start.X);
        SelectionRectangle.Height = Math.Abs(current.Y - start.Y);
    }

    private void SelectionLayer_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_selectionStart is not { } start) return;
        SelectionLayer.ReleaseMouseCapture();
        _selectionStart = null;
        var end = e.GetPosition(SelectionLayer);
        var region = ToPixelRect(start, end, SelectionLayer.ActualWidth, SelectionLayer.ActualHeight,
            _screenshot.PixelWidth, _screenshot.PixelHeight);
        if (region.Width < 20 || region.Height < 20)
        {
            SelectionRectangle.Visibility = Visibility.Collapsed;
            return;
        }
        var cropped = new CroppedBitmap(_screenshot, region);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(cropped));
        using var output = new MemoryStream();
        encoder.Save(output);
        SelectedPng = output.ToArray();
        DialogResult = true;
    }

    public static Int32Rect ToPixelRect(System.Windows.Point start, System.Windows.Point end,
        double viewWidth, double viewHeight, int imageWidth, int imageHeight)
    {
        var scaleX = imageWidth / viewWidth;
        var scaleY = imageHeight / viewHeight;
        var x1 = Math.Clamp((int)Math.Floor(Math.Min(start.X, end.X) * scaleX), 0, imageWidth);
        var y1 = Math.Clamp((int)Math.Floor(Math.Min(start.Y, end.Y) * scaleY), 0, imageHeight);
        var x2 = Math.Clamp((int)Math.Ceiling(Math.Max(start.X, end.X) * scaleX), 0, imageWidth);
        var y2 = Math.Clamp((int)Math.Ceiling(Math.Max(start.Y, end.Y) * scaleY), 0, imageHeight);
        return new Int32Rect(x1, y1, x2 - x1, y2 - y1);
    }

    private void SelectionLayer_MouseRightButtonDown(object sender, MouseButtonEventArgs e) => DialogResult = false;

    private void Window_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Escape) DialogResult = false;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int width, int height, uint flags);
}
