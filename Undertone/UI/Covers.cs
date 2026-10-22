using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Undertone.UI
{
    /// <summary>Загрузка обложек с кэшем; BitmapImage качает http асинхронно сам.</summary>
    public static class Covers
    {
        private static readonly Dictionary<string, BitmapImage> Cache = new();
        private const int MaxCache = 600;

        public static ImageSource? Load(string? url, int decodeWidth)
        {
            if (string.IsNullOrEmpty(url)) return null;
            var key = $"{decodeWidth}|{url}";
            if (Cache.TryGetValue(key, out var cached)) return cached;
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.UriSource = new Uri(url, UriKind.Absolute);
                bmp.DecodePixelWidth = decodeWidth;
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                bmp.EndInit();
                if (Cache.Count >= MaxCache) Cache.Clear();
                Cache[key] = bmp;
                return bmp;
            }
            catch (Exception ex) when (ex is UriFormatException or NotSupportedException or ArgumentException)
            {
                return null;
            }
        }
    }

    /// <summary>{Binding Cover, Converter={StaticResource CoverConverter}, ConverterParameter=88}</summary>
    public sealed class CoverConverter : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            int size = 100;
            if (parameter != null) int.TryParse(parameter.ToString(), out size);
            return Covers.Load(value as string, size);
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }
}
