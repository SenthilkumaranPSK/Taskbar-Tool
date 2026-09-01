using System.IO;
using System.Windows.Media.Imaging;
using TaskbarMediaWidget.Core;
using Windows.Storage.Streams;

namespace TaskbarMediaWidget.Media;

internal static class ThumbnailConverter
{
    /// <summary>
    /// Converts an SMTC thumbnail stream reference into a frozen (cross-thread-safe) BitmapImage,
    /// or null if there's no thumbnail / it fails to decode.
    /// </summary>
    public static async Task<BitmapImage?> TryConvertAsync(IRandomAccessStreamReference? thumbnail, int decodePixelWidth = 64)
    {
        if (thumbnail is null)
        {
            return null;
        }

        try
        {
            using var winrtStream = await thumbnail.OpenReadAsync();

            using var reader = new DataReader(winrtStream);
            await reader.LoadAsync((uint)winrtStream.Size);
            var bytes = new byte[winrtStream.Size];
            reader.ReadBytes(bytes);

            using var memoryStream = new MemoryStream(bytes);

            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.DecodePixelWidth = decodePixelWidth;
            bitmap.StreamSource = memoryStream;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Failed to decode media thumbnail: {ex.Message}");
            return null;
        }
    }
}
