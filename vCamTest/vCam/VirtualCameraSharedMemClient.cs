using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace vCam
{
    public class VirtualCameraSharedMemClient : IDisposable
    {
        private const string SHM_NAME = "Global\\vCamShm";
        private const int FRAME_W = 1920;
        private const int FRAME_H = 1080;
        private const int BYTES_PER_PIXEL = 3; // BGR24
        private const int FRAME_SZ = FRAME_W * FRAME_H * BYTES_PER_PIXEL; // 6 220 800
        private const int HEADER_SZ = 8; // int frameId + uint dataSize
        private const int TOTAL_SZ = HEADER_SZ + FRAME_SZ;

        private MemoryMappedFile? _mmf;
        private MemoryMappedViewAccessor? _accessor;
        private int _frameId = 0;
        private bool _isConnected = false;

        public event EventHandler<string>? StatusChanged;
        public event EventHandler<string>? ErrorOccurred;

        public bool IsConnected => _isConnected;
        public int FrameWidth => FRAME_W;
        public int FrameHeight => FRAME_H;

        public Task<bool> ConnectAsync()
        {
            try
            {
                // Убеждаемся, что предыдущий экземпляр закрыт
                if (_mmf != null)
                {
                    try { _accessor?.Dispose(); } catch { }
                    try { _mmf?.Dispose(); } catch { }
                    _mmf = null;
                    _accessor = null;
                    GC.Collect(); // Дополнительная гарантия освобождения ресурсов
                    GC.WaitForPendingFinalizers();
                }

                // Явно указываем размер и перезаписываем, если существует
                _mmf = MemoryMappedFile.CreateOrOpen(SHM_NAME, TOTAL_SZ, MemoryMappedFileAccess.ReadWrite);
                _accessor = _mmf.CreateViewAccessor(0, TOTAL_SZ, MemoryMappedFileAccess.Write);
                
                // Инициализация с нулями
                _accessor.Write(0, 0); // frameId = 0
                _accessor.Write(4, (uint)FRAME_SZ); // dataSize = FRAME_SZ

                _isConnected = true;
                OnStatusChanged($"Shared memory открыта: {SHM_NAME}, размер {TOTAL_SZ} байт");
                return Task.FromResult(true);
            }
            catch (Exception ex)
            {
                OnErrorOccurred($"Ошибка открытия shared memory: {ex.Message}");
                return Task.FromResult(false);
            }
        }

        public async Task<bool> SendFrameAsync(byte[] jpegData)
        {
            if (!_isConnected || _accessor == null)
            {
                OnErrorOccurred("Не подключен к виртуальной камере");
                return false;
            }

            try
            {
                // Конвертация JPEG -> Bitmap
                using var ms = new MemoryStream(jpegData);
                using var originalBitmap = new Bitmap(ms);

                // Масштабирование
                using var resizedBitmap = new Bitmap(FRAME_W, FRAME_H);
                using (var graphics = Graphics.FromImage(resizedBitmap))
                {
                    graphics.DrawImage(originalBitmap, 0, 0, FRAME_W, FRAME_H);
                }

                byte[] imageData = ConvertToBGR24(resizedBitmap);

                if (imageData.Length != FRAME_SZ)
                {
                    throw new InvalidOperationException($"Размер кадра {imageData.Length}, ожидается {FRAME_SZ}");
                }

                // Сначала записываем данные кадра (чтобы избежать гонки условий)
                _accessor.WriteArray(HEADER_SZ, imageData, 0, imageData.Length);
                
                // Записываем размер данных
                _accessor.Write(4, (uint)FRAME_SZ);
                
                // Thread barrier чтобы убедиться, что запись данных и размера завершена
                System.Threading.Thread.MemoryBarrier();
                
                // В последнюю очередь увеличиваем счетчик кадров,
                // что сигнализирует фильтру о доступности нового кадра
                _accessor.Write(0, ++_frameId);

                OnStatusChanged($"Отправлен кадр #{_frameId}, размер {FRAME_SZ} байт");
                return true;
            }
            catch (Exception ex)
            {
                OnErrorOccurred($"Ошибка отправки кадра: {ex.Message}");
                return false;
            }
        }

        private byte[] ConvertToBGR24(Bitmap bitmap)
        {
            var rect = new Rectangle(0, 0, FRAME_W, FRAME_H);
            var bmpData = bitmap.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
            try
            {
                int strideBytes = Math.Abs(bmpData.Stride);
                byte[] raw = new byte[strideBytes * FRAME_H];
                Marshal.Copy(bmpData.Scan0, raw, 0, raw.Length);

                byte[] bgr24 = new byte[FRAME_SZ];

                for (int y = 0; y < FRAME_H; y++)
                {
                    int srcY = FRAME_H - 1 - y; // bottom-up
                    for (int x = 0; x < FRAME_W; x++)
                    {
                        int srcX = FRAME_W - 1 - x; // mirror X
                        int srcIndex = srcY * strideBytes + srcX * 3;
                        int dstIndex = (y * FRAME_W + x) * 3;
                        bgr24[dstIndex] = raw[srcIndex];
                        bgr24[dstIndex + 1] = raw[srcIndex + 1];
                        bgr24[dstIndex + 2] = raw[srcIndex + 2];
                    }
                }
                return bgr24;
            }
            finally
            {
                bitmap.UnlockBits(bmpData);
            }
        }

        public Task DisconnectAsync()
        {
            _accessor?.Dispose();
            _mmf?.Dispose();
            _isConnected = false;
            OnStatusChanged("Shared memory закрыта");
            return Task.CompletedTask;
        }

        protected void OnStatusChanged(string msg) => StatusChanged?.Invoke(this, msg);
        protected void OnErrorOccurred(string msg) => ErrorOccurred?.Invoke(this, msg);

        public void Dispose()
        {
            DisconnectAsync().Wait();
        }
    }
} 