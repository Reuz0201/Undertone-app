using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Undertone.UI
{
    /// <summary>
    /// Загрузка обложек с кэшем (LRU на 600 штук); BitmapImage качает http асинхронно сам.
    /// Неудачная загрузка выкидывается из кэша — при следующем показе обложка скачается заново,
    /// а не останется пустой до перезапуска. Работает только из UI-потока.
    /// </summary>
    public static class Covers
    {
        private const int MaxCache = 600;
        private static readonly Dictionary<string, LinkedListNode<(string Key, BitmapImage Image)>> Cache = new();
        private static readonly LinkedList<(string Key, BitmapImage Image)> Order = new();

        public static ImageSource? Load(string? url, int decodeWidth)
        {
            if (string.IsNullOrEmpty(url)) return null;
            var key = $"{decodeWidth}|{url}";
            if (Cache.TryGetValue(key, out var node))
            {
                // недавно использованное — в начало
                Order.Remove(node);
                Order.AddFirst(node);
                return node.Value.Image;
            }
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.UriSource = new Uri(Distinct(url, decodeWidth), UriKind.Absolute);
                bmp.DecodePixelWidth = decodeWidth;
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                bmp.DownloadFailed += (_, _) => Forget(key, bmp);
                bmp.DecodeFailed += (_, _) => Forget(key, bmp);
                bmp.EndInit();

                var added = Order.AddFirst((key, bmp));
                Cache[key] = added;
                while (Cache.Count > MaxCache && Order.Last is { } last)
                {
                    Order.RemoveLast();
                    Cache.Remove(last.Value.Key);
                }
                return bmp;
            }
            catch (Exception ex) when (ex is UriFormatException or NotSupportedException or ArgumentException)
            {
                return null;
            }
        }

        /// <summary>
        /// Своя ссылка на каждый размер. WPF качает картинки через общий кэш по адресу, и две одновременные
        /// загрузки одного адреса (у Spotify одна ссылка на все размеры: большая обложка, нижняя панель, фон)
        /// мешают друг другу — обе падают, и вместо обложки остаётся пустой квадрат. Лишний параметр серверы
        /// Яндекса и Spotify пропускают мимо.
        /// </summary>
        internal static string Distinct(string url, int decodeWidth)
        {
            if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                return url;
            return url + (url.Contains('?') ? "&" : "?") + "ut=" + decodeWidth;
        }

        private static void Forget(string key, BitmapImage bmp)
        {
            if (!Cache.TryGetValue(key, out var node) || !ReferenceEquals(node.Value.Image, bmp)) return;
            Order.Remove(node);
            Cache.Remove(key);
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
