using System;
using System.IO;
using NAudio.Wave;
using Undertone.Core;

namespace Undertone.Audio
{
    /// <summary>Закачка оборвалась насовсем — играть дальше нечего. С самим файлом всё в порядке.</summary>
    public sealed class StreamInterruptedException : IOException
    {
        public StreamInterruptedException(Exception? inner)
            : base("связь оборвалась во время загрузки трека", inner) { }
    }

    /// <summary>
    /// Потоковое воспроизведение MP3, который ещё качается: читает .part-файл закачки покадрово
    /// и раскодирует через ACM (встроенный в Windows MP3-кодек).
    ///
    /// Если данных пока нет, отдаёт тишину и не двигает позицию (состояние «буферизация»),
    /// а не блокирует поток вывода звука. Перемотка — по средней скорости потока (байт на секунду),
    /// после неё декодер сам находит ближайший заголовок кадра.
    ///
    /// Не потокобезопасен: AudioEngine вызывает Read и Position под своей блокировкой.
    /// </summary>
    public sealed class StreamingMp3Reader : WaveStream
    {
        /// <summary>Сколько байт должно быть скачано за текущей позицией, чтобы безопасно читать кадр.</summary>
        private const int FrameGuardBytes = 8 * 1024;
        private const int StartWaitMs = 15_000;

        private readonly CacheDownload _download;
        private readonly FileStream _file;
        private readonly IMp3FrameDecompressor _decompressor;
        private readonly WaveFormat _format;
        private readonly long _dataStart;
        private readonly double _sourceBytesPerSecond;
        private readonly long _pcmLength;
        private readonly byte[] _decoded = new byte[64 * 1024];
        private int _decodedOffset;
        private int _decodedCount;
        private long _pcmPosition;
        private bool _ended;
        private bool _disposed;

        /// <summary>Данных не хватает — играет тишина, ждём сеть.</summary>
        public bool IsStarved { get; private set; }

        public StreamingMp3Reader(CacheDownload download, TimeSpan duration)
        {
            _download = download;
            _download.AddReader();
            _file = new FileStream(download.PartPath, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 1 << 15);
            try
            {
                SkipId3v2();
                if (!WaitBytes(_file.Position + FrameGuardBytes, StartWaitMs))
                    throw new TimeoutException("Начало трека не загрузилось");

                var first = Mp3Frame.LoadFromStream(_file)
                            ?? throw new InvalidDataException("В файле нет MP3-кадров");
                var channels = first.ChannelMode == ChannelMode.Mono ? 1 : 2;
                _decompressor = new AcmMp3FrameDecompressor(
                    new Mp3WaveFormat(first.SampleRate, channels, first.FrameLength, first.BitRate));
                _format = _decompressor.OutputFormat;
                _dataStart = first.FileOffset;

                // средняя скорость потока: по размеру файла и длительности (верно и для VBR), иначе — по битрейту кадра
                var total = download.TotalBytes;
                _sourceBytesPerSecond = duration > TimeSpan.FromSeconds(1) && total > _dataStart
                    ? (total - _dataStart) / duration.TotalSeconds
                    : Math.Max(1, first.BitRate / 8.0);
                var seconds = duration > TimeSpan.Zero
                    ? duration.TotalSeconds
                    : total > 0 ? (total - _dataStart) / _sourceBytesPerSecond : 0;
                _pcmLength = Align((long)(seconds * _format.AverageBytesPerSecond));

                // первый кадр VBR-файла — служебный заголовок Xing/Info, звука в нём нет
                if (XingHeader.LoadXingHeader(first) == null) Decode(first);
            }
            catch
            {
                _decompressor?.Dispose();
                _file.Dispose();
                _download.RemoveReader();
                throw;
            }
        }

        public override WaveFormat WaveFormat => _format;

        public override long Length => _pcmLength;

        public override long Position
        {
            get => _pcmPosition;
            set
            {
                var target = Align(Math.Clamp(value, 0, Math.Max(0, _pcmLength)));
                var seconds = (double)target / _format.AverageBytesPerSecond;
                var offset = _dataStart + (long)(seconds * _sourceBytesPerSecond);
                var total = _download.TotalBytes;
                if (total > 0) offset = Math.Min(offset, total - 1);
                _file.Position = Math.Max(_dataStart, offset);
                _decompressor.Reset();
                _decodedCount = 0;
                _decodedOffset = 0;
                _pcmPosition = target;
                _ended = false;
            }
        }

        /// <summary>Какая доля файла уже скачана (для полосы буфера). 1 — скачан целиком.</summary>
        public double BufferedFraction => _download.Fraction < 0 ? 0 : _download.Fraction;

        public override int Read(byte[] buffer, int offset, int count)
        {
            int written = 0;
            while (written < count)
            {
                if (_decodedCount > 0)
                {
                    var n = Math.Min(_decodedCount, count - written);
                    Buffer.BlockCopy(_decoded, _decodedOffset, buffer, offset + written, n);
                    _decodedOffset += n;
                    _decodedCount -= n;
                    _pcmPosition += n;
                    written += n;
                    continue;
                }
                if (_ended) break;

                if (!HasFrameData())
                {
                    if (_download.IsFaulted)
                        throw new StreamInterruptedException(_download.Completion.Exception?.InnerException);
                    // данных нет: тишина вместо зависания потока вывода, позиция стоит на месте
                    IsStarved = true;
                    Array.Clear(buffer, offset + written, count - written);
                    return count;
                }
                IsStarved = false;

                var start = _file.Position;
                var frame = Mp3Frame.LoadFromStream(_file);
                if (frame == null)
                {
                    if (_download.IsCompleted)
                    {
                        _ended = true; // конец файла — трек доиграл
                        break;
                    }
                    // поиск заголовка упёрся в конец скачанного — подождём ещё данных
                    _file.Position = start;
                    IsStarved = true;
                    Array.Clear(buffer, offset + written, count - written);
                    return count;
                }
                Decode(frame);
            }
            return written;
        }

        private void Decode(Mp3Frame frame)
        {
            try
            {
                _decodedCount = _decompressor.DecompressFrame(frame, _decoded, 0);
                _decodedOffset = 0;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // битый кадр (обычно сразу после перемотки) — пропускаем, следующий раскодируется
                _decodedCount = 0;
                _decompressor.Reset();
            }
        }

        private bool HasFrameData()
        {
            if (_download.IsCompleted) return !_download.IsFaulted || _download.Written - _file.Position >= FrameGuardBytes;
            return _download.Written - _file.Position >= FrameGuardBytes;
        }

        private bool WaitBytes(long bytes, int timeoutMs) =>
            _download.WaitFor(bytes, timeoutMs) && (_download.Written >= bytes || _download.IsCompleted && !_download.IsFaulted);

        /// <summary>ID3v2 в начале файла пропускаем по размеру из заголовка, не читая сам тег (там бывает обложка).</summary>
        private void SkipId3v2()
        {
            if (!WaitBytes(10, StartWaitMs)) throw new TimeoutException("Начало трека не загрузилось");
            var header = new byte[10];
            _file.Position = 0;
            if (_file.Read(header, 0, 10) == 10 && header[0] == 'I' && header[1] == 'D' && header[2] == '3')
            {
                var size = (header[6] & 0x7F) << 21 | (header[7] & 0x7F) << 14 | (header[8] & 0x7F) << 7 | (header[9] & 0x7F);
                var footer = (header[5] & 0x10) != 0 ? 10 : 0;
                var end = 10L + size + footer;
                if (!WaitBytes(end, StartWaitMs)) throw new TimeoutException("Начало трека не загрузилось");
                _file.Position = end;
            }
            else
            {
                _file.Position = 0;
            }
        }

        private long Align(long pcmBytes) => pcmBytes - pcmBytes % _format.BlockAlign;

        protected override void Dispose(bool disposing)
        {
            if (!_disposed && disposing)
            {
                _disposed = true;
                _decompressor.Dispose();
                _file.Dispose();
                _download.RemoveReader();
            }
            base.Dispose(disposing);
        }
    }
}
