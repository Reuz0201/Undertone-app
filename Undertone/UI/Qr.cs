using System;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using QRCoder;
using Undertone.Core;

namespace Undertone.UI
{
    /// <summary>QR-код картинкой (чёрное на белом — его сканирует любая камера).</summary>
    public static class Qr
    {
        public static ImageSource? Make(string text, int pixelsPerModule = 8)
        {
            try
            {
                using var gen = new QRCodeGenerator();
                using var data = gen.CreateQrCode(text, QRCodeGenerator.ECCLevel.M);
                var png = new PngByteQRCode(data).GetGraphic(pixelsPerModule);
                var img = new BitmapImage();
                img.BeginInit();
                img.StreamSource = new MemoryStream(png);
                img.CacheOption = BitmapCacheOption.OnLoad;
                img.EndInit();
                img.Freeze();
                return img;
            }
            catch (Exception ex)
            {
                Log.Warn("QR не нарисовался", ex);
                return null;
            }
        }
    }
}
