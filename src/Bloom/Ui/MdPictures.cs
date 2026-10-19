using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Bloom.Ui;

/// The Markdown view's pictures on this PC (MdText: a path DocLink.Picture let through): the file's header read off the
/// UI thread (its size: the room it takes, reserved by its aspect), then the picture decoded off the UI thread at the
/// width it is drawn at rounded up to a WidthStep (at most its own; never wider than PictureWidth), frozen, kept by
/// path, last write and width — up to KeepBytes, and only while a view is open. The file is read whole and let go
/// (OnLoad). One that can't be read shows as its alt text until the next Show looks again (LookAgain).
internal static class MdPictures
{
    /// Pictures wider than this are decoded at this width (memory).
    public const int PictureWidth = 1600;

    /// Decoded pictures kept up to this many bytes, the least recently used let go first.
    private const long KeepBytes = 64L << 20;

    /// Decode widths go up in steps of this many pixels: a window resized doesn't decode a picture for every width.
    public const int WidthStep = 128;

    private sealed record Header(DateTime Written, int Width, int Height, double DpiX, double DpiY, bool Missing);

    private sealed record Kept((string, DateTime, int) Key, BitmapRef Bitmap, long Bytes);

    private static readonly Dictionary<string, Header> headers = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<(string, DateTime, int), LinkedListNode<Kept>> decoded = [];
    private static readonly LinkedList<Kept> order = new(); // least recently used first
    private static long bytes;
    private static readonly HashSet<object> views = []; // the views open (Open, Closed)

    /// What is known of the picture at `path` from an earlier read: null if nothing yet.
    public static (bool Missing, int Width, int Height)? Known(string path) => headers.TryGetValue(path, out var h) ? (h.Missing, h.Width, h.Height) : null;

    /// The pictures that couldn't be read forgotten: read again when next shown (added or unlocked since).
    public static void LookAgain()
    {
        foreach (var path in headers.Where(h => h.Value.Missing).Select(h => h.Key).ToList()) headers.Remove(path);
    }

    /// A view showing pictures is open.
    public static void Open(object view) => views.Add(view);

    /// A view closed: with the last one, the decoded pictures and what was read let go.
    public static void Closed(object view)
    {
        if (!views.Remove(view) || views.Count > 0) return;
        decoded.Clear();
        order.Clear();
        bytes = 0;
        headers.Clear();
    }

    /// `p` as last read (its size: laid out right away); Read still looks again.
    public static void Fill(MdPicture p)
    {
        if (headers.TryGetValue(p.Path, out var h)) Put(p, h);
    }

    private static void Put(MdPicture p, Header h)
    {
        p.Missing = h.Missing;
        p.Written = h.Written;
        p.PixelWidth = h.Width;
        p.PixelHeight = h.Height;
        // its size in the view: its pixels at its own DPI (96 when the file says none)
        p.DipWidth = h.Width * 96 / (h.DpiX > 1 ? h.DpiX : 96);
        p.DipHeight = h.Height * 96 / (h.DpiY > 1 ? h.DpiY : 96);
        p.Ready = !h.Missing;
    }

    /// `p`'s header read again (the file may have changed); `changed` on the UI thread when it isn't what p has (the
    /// text is shaped again, its pictures filled from this read).
    public static void Read(MdPicture p, Dispatcher ui, Action changed)
    {
        string path = p.Path;
        Task.Run(() =>
        {
            Header h;
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists) h = new Header(default, 0, 0, 96, 96, true);
                else
                {
                    using var stream = File.OpenRead(path);
                    var frame = BitmapFrame.Create(stream, BitmapCreateOptions.DelayCreation | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.None);
                    h = new Header(info.LastWriteTimeUtc, frame.PixelWidth, frame.PixelHeight, frame.DpiX, frame.DpiY, frame.PixelWidth <= 0 || frame.PixelHeight <= 0);
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException or FormatException or InvalidOperationException or System.Runtime.InteropServices.ExternalException)
            {
                h = new Header(default, 0, 0, 96, 96, true);
            }
            ui.BeginInvoke(() =>
            {
                headers[path] = h;
                bool same = p.Missing == h.Missing && p.Ready != h.Missing && p.Written == h.Written && p.PixelWidth == h.Width && p.PixelHeight == h.Height;
                Put(p, h);
                if (!same) changed();
            });
        });
    }

    /// `p` decoded `width` pixels wide (for its laid-out width × the DPI), from the cache or off the UI thread; `done` on
    /// the UI thread when it came from the file.
    public static void Decode(MdPicture p, double drawnPixels, Dispatcher ui, Action done)
    {
        int width = Math.Max(1, Math.Min(Math.Min(p.PixelWidth, PictureWidth), (int)Math.Ceiling(drawnPixels / WidthStep) * WidthStep));
        if (p.Bitmap?.Width == width) return;
        var key = (p.Path, p.Written, width);
        if (decoded.TryGetValue(key, out var hit))
        {
            order.Remove(hit);
            order.AddLast(hit);
            p.Bitmap = hit.Value.Bitmap;
            return;
        }
        if (p.Decoding == width) return;
        p.Decoding = width;
        string path = p.Path;
        bool smaller = width < p.PixelWidth; // read here: p is the UI thread's
        Task.Run(() =>
        {
            BitmapSource? bitmap = null;
            try
            {
                using var stream = File.OpenRead(path);
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                image.StreamSource = stream;
                if (smaller) image.DecodePixelWidth = width;
                image.EndInit();
                image.Freeze();
                bitmap = image;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException or FormatException or InvalidOperationException or System.Runtime.InteropServices.ExternalException)
            {
                bitmap = null;
            }
            ui.BeginInvoke(() =>
            {
                if (p.Decoding == width) p.Decoding = 0;
                if (bitmap is null)
                {
                    p.Missing = true;
                    p.Ready = false;
                    headers[path] = new Header(p.Written, 0, 0, 96, 96, true);
                    done();
                    return;
                }
                if (decoded.TryGetValue(key, out var same)) // decoded twice at once (two texts with the picture): the first kept
                {
                    p.Bitmap = same.Value.Bitmap;
                    done();
                    return;
                }
                var made = new BitmapRef(bitmap, width);
                p.Bitmap = made;
                if (views.Count > 0)
                {
                    long n = (long)bitmap.PixelWidth * bitmap.PixelHeight * ((bitmap.Format.BitsPerPixel + 7) / 8);
                    decoded[key] = order.AddLast(new Kept(key, made, n));
                    bytes += n;
                    while (bytes > KeepBytes && order.First is { } oldest && oldest != order.Last)
                    {
                        order.RemoveFirst();
                        decoded.Remove(oldest.Value.Key);
                        bytes -= oldest.Value.Bytes;
                    }
                }
                done();
            });
        });
    }
}
