using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace G350ConnectionManager.Services;

/// <summary>
/// Quality settings for the screen pipeline.
///
/// Everything here is applied on the PC, deliberately. Measured on the console,
/// ffmpeg needs ~1.0 s per frame at 480x360 and ~1.5 s at full size on its four
/// weak cores, which competes directly with whatever game is running. Encoding
/// here costs 1-6 ms, so the device only ever has to hand over the raw frame it
/// already captures.
/// </summary>
public sealed record CaptureSettings
{
    /// <summary>Output width; height keeps the console's 4:3 aspect.</summary>
    public int Width { get; init; } = 640;

    /// <summary>Frames per second the preview aims for.</summary>
    public double Fps { get; init; } = 6;

    /// <summary>JPEG quality, 10-100. Higher is larger and slower to send.</summary>
    public int JpegQuality { get; init; } = 75;

    /// <summary>
    /// Extra pause after each frame, in ms. A manual throttle on top of the
    /// frame rate, for links that are busy or to trade latency for smoothness.
    /// </summary>
    public int DelayMs { get; init; } = 0;

    public static CaptureSettings Default => new();

    /// <summary>Height that matches <see cref="Width"/> at the console's 4:3.</summary>
    public int Height => Math.Max(2, (Width * 3 / 4) & ~1);

    /// <summary>Total time one frame is allowed to occupy, in ms.</summary>
    public double FrameBudgetMs => (1000.0 / Math.Max(0.5, Fps)) + DelayMs;

    /// <summary>
    /// Frame rate after the per-frame delay is taken into account.
    ///
    /// Delay is an extra pause on top of the frame budget, so the effective rate
    /// is lower than <see cref="Fps"/>. Recording needs this: the encoder is
    /// told a single rate for the whole file, and using the nominal value would
    /// play the result back too fast.
    /// </summary>
    public double EffectiveFps => FrameBudgetMs <= 0 ? Fps : 1000.0 / FrameBudgetMs;
}

/// <summary>
/// Turns raw console frames into display-ready and JPEG-encoded images.
///
/// The console always sends full-size BGRA; scaling and encoding happen here.
/// </summary>
public static class FrameConverter
{
    /// <summary>
    /// Converts a raw frame to a bitmap at the requested size.
    ///
    /// Bgr32 is used rather than Bgra32: the console stores alpha = 0 in every
    /// pixel, and WPF honours alpha when compositing, so a Bgra32 source renders
    /// as a fully transparent (black) rectangle.
    /// </summary>
    public static BitmapSource ToBitmap(CapturedFrame frame, CaptureSettings settings)
    {
        var source = BitmapSource.Create(
            frame.Width, frame.Height, 96, 96,
            PixelFormats.Bgr32, null, frame.Pixels, frame.Stride);
        source.Freeze();

        if (settings.Width == frame.Width)
            return source;

        var scaled = new TransformedBitmap(
            source,
            new ScaleTransform(
                (double)settings.Width / frame.Width,
                (double)settings.Height / frame.Height));
        scaled.Freeze();
        return scaled;
    }

    /// <summary>
    /// Scales a raw frame to the requested size and returns packed BGRA rows,
    /// ready to pipe into ffmpeg.
    /// </summary>
    public static byte[] ToRawBgra(CapturedFrame frame, int width, int height)
    {
        if (frame.Width == width && frame.Height == height)
            return frame.Pixels;

        var bitmap = ToBitmap(frame, new CaptureSettings { Width = width });
        var stride = bitmap.PixelWidth * 4;
        var buffer = new byte[stride * bitmap.PixelHeight];
        bitmap.CopyPixels(buffer, stride, 0);
        return buffer;
    }

    /// <summary>Encodes a frame as JPEG at the configured quality.</summary>
    public static byte[] ToJpeg(CapturedFrame frame, CaptureSettings settings)
    {
        var bitmap = ToBitmap(frame, settings);

        var encoder = new JpegBitmapEncoder
        {
            // WPF's quality scale is 1-100, which maps sensibly to the UI slider.
            QualityLevel = Math.Clamp(settings.JpegQuality, 10, 100)
        };
        encoder.Frames.Add(BitmapFrame.Create(bitmap));

        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }
}
