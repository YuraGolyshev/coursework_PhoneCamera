using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace vCam
{
    public class VirtualCameraTcpClient : IDisposable
    {
        private TcpClient _client;
        private NetworkStream _stream;
        private int _width = 640;
        private int _height = 480;
        private int _bytesPerPixel = 3; // RGB24 формат по умолчанию
        private bool _isConnected = false;

        public event EventHandler<string>? StatusChanged;
        public event EventHandler<string>? ErrorOccurred;

        public bool IsConnected => _isConnected;
        public int FrameWidth => _width;
        public int FrameHeight => _height;

        public async Task<bool> ConnectAsync()
        {
            try
            {
                Console.WriteLine($"[TCP] {DateTime.Now:HH:mm:ss.fff} STATUS: Подключение к 127.0.0.1:9090...");
                OnStatusChanged($"Подключение к 127.0.0.1:9090...");
                
                _client = new TcpClient();
                await _client.ConnectAsync("127.0.0.1", 9090);
                _stream = _client.GetStream();

                // Читаем заголовок информации (11 байт)
                var header = new byte[11];
                var totalBytesRead = 0;
                
                while (totalBytesRead < 11)
                {
                    var bytesRead = await _stream.ReadAsync(header, totalBytesRead, 11 - totalBytesRead);
                    if (bytesRead == 0)
                        throw new Exception("Сервер закрыл соединение");
                    totalBytesRead += bytesRead;
                }

                Console.WriteLine($"[TCP] Получен заголовок: {BitConverter.ToString(header)}");

                // Проверяем заголовок
                if (header[0] != 0xFF)
                    throw new Exception($"Неожиданный заголовок: нужен 0xFF, получен {header[0]:X2}");
                if (header[1] != 0x02)
                    throw new Exception($"Неожиданный заголовок: нужен 0x02, получен {header[1]:X2}");

                // Читаем размеры в Big Endian формате (как в оригинальном коде)
                _width = (header[2] << 24) | (header[3] << 16) | (header[4] << 8) | header[5];
                _height = (header[6] << 24) | (header[7] << 16) | (header[8] << 8) | header[9];
                
                // Вычисляем BytesPerPixel из BitsPerPixel
                var bitsPerPixel = header[10];
                _bytesPerPixel = (bitsPerPixel + 7) / 8;

                Console.WriteLine($"[TCP] ПРАВИЛЬНО ДЕКОДИРОВАНО:");
                Console.WriteLine($"[TCP] Ширина: {_width}");
                Console.WriteLine($"[TCP] Высота: {_height}");
                Console.WriteLine($"[TCP] Бит на пиксель: {bitsPerPixel}");
                Console.WriteLine($"[TCP] Байт на пиксель: {_bytesPerPixel}");
                Console.WriteLine($"[TCP] {DateTime.Now:HH:mm:ss.fff} STATUS: Подключено! Разрешение: {_width}x{_height}");
                
                _isConnected = true;
                OnStatusChanged($"Подключено! Разрешение: {_width}x{_height}");
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[TCP] {DateTime.Now:HH:mm:ss.fff} ERROR: {ex.Message}");
                OnErrorOccurred($"Ошибка подключения: {ex.Message}");
                return false;
            }
        }

        public async Task<bool> SendFrameAsync(byte[] jpegData)
        {
            if (!_isConnected || _stream == null)
            {
                OnErrorOccurred("Не подключен к виртуальной камере");
                return false;
            }

            try
            {
                Console.WriteLine($"[TCP] ===== ОТПРАВКА КАДРА =====");
                
                // Конвертируем JPEG в Bitmap
                using var ms = new MemoryStream(jpegData);
                using var originalBitmap = new Bitmap(ms);
                
                Console.WriteLine($"[TCP] Получен JPEG размер: {jpegData.Length} байт");
                Console.WriteLine($"[TCP] Оригинальный размер: {originalBitmap.Width}x{originalBitmap.Height}");
                
                // Масштабируем до нужного размера
                using var resizedBitmap = new Bitmap(_width, _height);
                using (var graphics = Graphics.FromImage(resizedBitmap))
                {
                    graphics.DrawImage(originalBitmap, 0, 0, _width, _height);
                }
                
                Console.WriteLine($"[TCP] Целевое разрешение: {_width}x{_height}");

                // Конвертируем в правильный формат пикселей
                byte[] imageData;
                
                if (_bytesPerPixel == 3) // BGR24 
                {
                    imageData = ConvertToRGB24(resizedBitmap);
                    Console.WriteLine($"[TCP] Конвертировано в BGR24 (bottom-up + зеркально): {imageData.Length} байт");
                }
                else if (_bytesPerPixel == 4) // BGR32
                {
                    imageData = ConvertToRGB32(resizedBitmap);
                    Console.WriteLine($"[TCP] Конвертировано в BGR32 (bottom-up + зеркально): {imageData.Length} байт");
                }
                else
                {
                    throw new Exception($"Неподдерживаемый формат: {_bytesPerPixel} байт/пиксель");
                }

                // Создаем протокольный заголовок (как в оригинальном клиенте)
                var size = imageData.Length;
                var protocolHeader = new byte[]
                {
                    0xFF,                       // Start of command
                    0x01,                       // Command (image data)
                    (byte)(size & 0xFF),        // Size 0 (Little Endian)
                    (byte)((size >> 8) & 0xFF), // Size 1
                    (byte)((size >> 16) & 0xFF) // Size 2
                };

                Console.WriteLine($"[TCP] Заголовок протокола: {BitConverter.ToString(protocolHeader)}");
                Console.WriteLine($"[TCP] Размер данных: {size} байт");
                
                // Отправляем заголовок и данные
                await _stream.WriteAsync(protocolHeader);
                await _stream.WriteAsync(imageData);
                await _stream.FlushAsync();

                Console.WriteLine($"[TCP] Кадр отправлен с правильным протоколом (BGR формат, bottom-up + зеркально)");
                Console.WriteLine($"[TCP] ========================");
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[TCP] {DateTime.Now:HH:mm:ss.fff} ERROR: {ex.Message}");
                OnErrorOccurred($"Ошибка отправки кадра: {ex.Message}");
                return false;
            }
        }

        private byte[] ConvertToRGB24(Bitmap bitmap)
        {
            var rect = new Rectangle(0, 0, _width, _height);
            var bmpData = bitmap.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);

            try
            {
                var bytes = new byte[Math.Abs(bmpData.Stride) * _height];
                Marshal.Copy(bmpData.Scan0, bytes, 0, bytes.Length);
                
                // Создаем точный массив RGB24 (без padding)
                var rgb24Data = new byte[_width * _height * 3];
                var stride = Math.Abs(bmpData.Stride);
                
                // DirectShow нужны ОБА преобразования: bottom-up И зеркальное отражение
                for (int y = 0; y < _height; y++)
                {
                    // Переворот по Y (bottom-up): читаем снизу, записываем сверху
                    var srcY = _height - 1 - y;
                    
                    for (int x = 0; x < _width; x++)
                    {
                        // Зеркальное отражение по X: читаем справа, записываем слева
                        var srcX = _width - 1 - x;
                        var srcIndex = srcY * stride + srcX * 3;
                        var dstIndex = (y * _width + x) * 3;
                        
                        // .NET Format24bppRgb хранит как BGR, но DirectShow тоже ожидает BGR24
                        rgb24Data[dstIndex] = bytes[srcIndex];     // B
                        rgb24Data[dstIndex + 1] = bytes[srcIndex + 1]; // G
                        rgb24Data[dstIndex + 2] = bytes[srcIndex + 2]; // R
                    }
                }
                
                Console.WriteLine($"[TCP] BGR24 (bottom-up + зеркально) первые байты: {rgb24Data[0]:X2} {rgb24Data[1]:X2} {rgb24Data[2]:X2}");
                return rgb24Data;
            }
            finally
            {
                bitmap.UnlockBits(bmpData);
            }
        }

        private byte[] ConvertToRGB32(Bitmap bitmap)
        {
            var rect = new Rectangle(0, 0, _width, _height);
            var bmpData = bitmap.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);

            try
            {
                var bytes = new byte[Math.Abs(bmpData.Stride) * _height];
                Marshal.Copy(bmpData.Scan0, bytes, 0, bytes.Length);
                var stride = Math.Abs(bmpData.Stride);

                // Конвертируем ARGB в BGR32 с переворотом
                var bgr32Data = new byte[_width * _height * 4];
                
                // DirectShow нужны ОБА преобразования: bottom-up И зеркальное отражение
                for (int y = 0; y < _height; y++)
                {
                    // Переворот по Y (bottom-up): читаем снизу, записываем сверху
                    var srcY = _height - 1 - y;
                    
                    for (int x = 0; x < _width; x++)
                    {
                        // Зеркальное отражение по X: читаем справа, записываем слева
                        var srcX = _width - 1 - x;
                        var srcIndex = srcY * stride + srcX * 4;
                        var dstIndex = (y * _width + x) * 4;
                        
                        // .NET Format32bppArgb хранит как BGRA, DirectShow ожидает BGRA32
                        bgr32Data[dstIndex] = bytes[srcIndex];     // B
                        bgr32Data[dstIndex + 1] = bytes[srcIndex + 1]; // G  
                        bgr32Data[dstIndex + 2] = bytes[srcIndex + 2]; // R
                        bgr32Data[dstIndex + 3] = 255;             // A (полная непрозрачность)
                    }
                }

                Console.WriteLine($"[TCP] BGR32 (bottom-up + зеркально) первые байты: {bgr32Data[0]:X2} {bgr32Data[1]:X2} {bgr32Data[2]:X2} {bgr32Data[3]:X2}");
                return bgr32Data;
            }
            finally
            {
                bitmap.UnlockBits(bmpData);
            }
        }

        public async Task DisconnectAsync()
        {
            try
            {
                _isConnected = false;
                _stream?.Close();
                _client?.Close();
                Console.WriteLine($"[TCP] {DateTime.Now:HH:mm:ss.fff} STATUS: Отключено");
                OnStatusChanged("Отключено от DirectShow виртуальной камеры");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[TCP] {DateTime.Now:HH:mm:ss.fff} ERROR при отключении: {ex.Message}");
                OnErrorOccurred($"Ошибка отключения: {ex.Message}");
            }
        }

        protected virtual void OnStatusChanged(string status)
        {
            StatusChanged?.Invoke(this, status);
        }

        protected virtual void OnErrorOccurred(string error)
        {
            ErrorOccurred?.Invoke(this, error);
        }

        public void Dispose()
        {
            DisconnectAsync().Wait(2000);
        }
    }
} 