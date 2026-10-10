using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Microsoft.CommandPalette.Extensions.Toolkit;
using NowPlaying.Core;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace NowPlaying.Services;

/// <summary>
/// Album art ready for the host: a 64 px (Dock/list icon) and a 200 px (details page) square PNG written to our
/// package cache folder. The host only receives file paths: no stream is shared across processes, and the details
/// page stays light (a large data: URI in markdown froze the host).
/// </summary>
internal sealed class ArtImage(string key, string iconPath, string largePath)
{
    public string Key { get; } = key;

    public IconInfo Icon { get; } = new(iconPath);

    /// <summary>file:/// URI of the 200 px image, for the details card.</summary>
    public string LargeUri { get; } = new Uri(largePath).AbsoluteUri;
}

internal static class ThumbnailLoader
{
    private const uint IconSize = 64;
    private const uint LargeSize = 200;
    private const ulong MaxSourceBytes = 8 * 1024 * 1024;
    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(3);

    /// <summary>Reads the raw thumbnail bytes and their SHA-256 (hex). Returns null if there is nothing usable.</summary>
    public static async Task<(byte[] Bytes, string Key)?> ReadAsync(IRandomAccessStreamReference reference)
    {
        using var source = await reference.OpenReadAsync().AsTask().WaitAsync(CallTimeout).ConfigureAwait(false);
        if (source.Size == 0 || source.Size > MaxSourceBytes)
        {
            return null;
        }

        var total = (uint)source.Size;
        var bytes = new byte[total];
        using (var reader = new DataReader(source.GetInputStreamAt(0)))
        {
            uint loaded = 0;
            while (loaded < total)
            {
                var n = await reader.LoadAsync(total - loaded).AsTask().WaitAsync(CallTimeout).ConfigureAwait(false);
                if (n == 0)
                {
                    return null;
                }

                loaded += n;
            }

            reader.ReadBytes(bytes);
            reader.DetachStream();
        }

        return (bytes, Convert.ToHexString(SHA256.HashData(bytes)));
    }

    /// <summary>Decodes, center-crops to a square and re-encodes as PNG at icon size and at details-page size.</summary>
    public static async Task<ArtImage?> BuildAsync(byte[] original, string key)
    {
        try
        {
            var dir = GetArtFolder();
            var iconPath = Path.Combine(dir, key + "-64.png");
            var largePath = Path.Combine(dir, key + "-200.png");
            if (!File.Exists(iconPath) || !File.Exists(largePath))
            {
                using var input = new InMemoryRandomAccessStream();
                using (var writer = new DataWriter(input.GetOutputStreamAt(0)))
                {
                    writer.WriteBytes(original);
                    await writer.StoreAsync();
                    writer.DetachStream();
                }

                input.Seek(0);
                var decoder = await BitmapDecoder.CreateAsync(input);
                await WriteFileAsync(await RenderSquareAsync(decoder, LargeSize), largePath).ConfigureAwait(false);
                await WriteFileAsync(await RenderSquareAsync(decoder, IconSize), iconPath).ConfigureAwait(false);
            }

            return new ArtImage(key, iconPath, largePath);
        }
        catch (Exception ex)
        {
            DiagLog.WriteLine($"Album art conversion failed: {ex.Message}");
            return null;
        }
    }

    private static readonly object FolderGate = new();
    private static string? _artFolder;

    // Package cache folder (a real path the host process can read); old files are removed on first use.
    // Not a Lazy<T>: a transient failure must not disable album art for the rest of the process lifetime.
    private static string GetArtFolder()
    {
        lock (FolderGate)
        {
            return _artFolder ??= CreateArtFolder();
        }
    }

    private static string CreateArtFolder()
    {
        string root;
        try
        {
            root = Windows.Storage.ApplicationData.Current.LocalCacheFolder.Path;
        }
        catch (Exception)
        {
            root = Path.GetTempPath(); // not packaged (tests / F5 without identity)
        }

        var dir = Path.Combine(root, "NowPlaying-art");
        Directory.CreateDirectory(dir);
        foreach (var file in Directory.EnumerateFiles(dir))
        {
            try
            {
                if (File.GetLastWriteTimeUtc(file) < DateTime.UtcNow.AddDays(-2))
                {
                    File.Delete(file);
                }
            }
            catch (IOException)
            {
                // In use or already gone; try again next start.
            }
            catch (UnauthorizedAccessException)
            {
                // Same as above.
            }
        }

        return dir;
    }

    private static async Task WriteFileAsync(InMemoryRandomAccessStream png, string path)
    {
        using (png)
        {
            var bytes = new byte[png.Size];
            using (var reader = new DataReader(png.GetInputStreamAt(0)))
            {
                await reader.LoadAsync((uint)png.Size);
                reader.ReadBytes(bytes);
                reader.DetachStream();
            }

            // Write to a unique temp name first so the host never reads a half-written file and two sessions
            // with the same art (same key) never share a temp file.
            var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            await File.WriteAllBytesAsync(temp, bytes).ConfigureAwait(false);
            try
            {
                File.Move(temp, path, overwrite: true);
            }
            catch (IOException) when (File.Exists(path))
            {
                // Another build of the same picture won (or the host has it open); its file is identical.
                File.Delete(temp);
            }
        }
    }

    private static async Task<InMemoryRandomAccessStream> RenderSquareAsync(BitmapDecoder decoder, uint size)
    {
        double scale = (double)size / Math.Min(decoder.PixelWidth, decoder.PixelHeight);
        uint scaledW = Math.Max(size, (uint)Math.Round(decoder.PixelWidth * scale));
        uint scaledH = Math.Max(size, (uint)Math.Round(decoder.PixelHeight * scale));

        var transform = new BitmapTransform
        {
            InterpolationMode = BitmapInterpolationMode.Fant,
            ScaledWidth = scaledW,
            ScaledHeight = scaledH,
            Bounds = new BitmapBounds
            {
                X = (scaledW - size) / 2,
                Y = (scaledH - size) / 2,
                Width = size,
                Height = size,
            },
        };

        var pixels = await decoder.GetPixelDataAsync(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Premultiplied,
            transform,
            ExifOrientationMode.IgnoreExifOrientation,
            ColorManagementMode.DoNotColorManage);

        var output = new InMemoryRandomAccessStream();
        try
        {
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, output);
            encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, size, size, 96, 96, pixels.DetachPixelData());
            await encoder.FlushAsync();
            output.Seek(0);
            return output;
        }
        catch
        {
            output.Dispose();
            throw;
        }
    }
}
