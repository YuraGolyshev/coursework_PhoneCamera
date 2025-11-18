using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

namespace vCam
{
    /// <summary>
    /// Отладочный TCP клиент для виртуальной камеры
    /// Помогает понять правильный протокол подключения
    /// </summary>
    public class VirtualCameraDebugClient : IDisposable
    {
        private TcpClient? _tcpClient;
        private NetworkStream? _networkStream;
        private readonly string _serverAddress;
        private readonly int _serverPort;
        private bool _isConnected;

        public event EventHandler<string>? StatusChanged;
        public event EventHandler<string>? ErrorOccurred;

        public bool IsConnected => _isConnected;

        public VirtualCameraDebugClient(string serverAddress = "127.0.0.1", int serverPort = 9090)
        {
            _serverAddress = serverAddress;
            _serverPort = serverPort;
        }

        /// <summary>
        /// Подключение к виртуальной камере с отладкой протокола
        /// </summary>
        public async Task<bool> ConnectAsync()
        {
            try
            {
                OnStatusChanged($"Подключение к {_serverAddress}:{_serverPort}...");
                
                _tcpClient = new TcpClient();
                await _tcpClient.ConnectAsync(_serverAddress, _serverPort);
                _networkStream = _tcpClient.GetStream();
                
                OnStatusChanged("TCP соединение установлено");

                // Читаем начальные данные от сервера
                await ReadInitialServerData();

                // Отправляем тестовые данные
                await SendTestData();

                _isConnected = true;
                OnStatusChanged("Подключение успешно!");
                return true;
            }
            catch (Exception ex)
            {
                OnErrorOccurred($"Ошибка подключения: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Чтение начальных данных от сервера
        /// </summary>
        private async Task ReadInitialServerData()
        {
            if (_networkStream == null) return;

            try
            {
                // Ждем данные от сервера
                byte[] buffer = new byte[1024];
                
                OnStatusChanged("Ожидание данных от сервера...");
                
                // Проверяем доступность данных
                int attempts = 0;
                while (!_networkStream.DataAvailable && attempts < 50)
                {
                    await Task.Delay(100);
                    attempts++;
                }

                if (_networkStream.DataAvailable)
                {
                    int bytesRead = await _networkStream.ReadAsync(buffer, 0, buffer.Length);
                    OnStatusChanged($"Получено {bytesRead} байт от сервера");
                    
                    // Выводим данные в hex и ASCII
                    LogDataReceived(buffer, bytesRead);
                }
                else
                {
                    OnStatusChanged("Сервер не отправил начальные данные");
                }
            }
            catch (Exception ex)
            {
                OnStatusChanged($"Ошибка чтения от сервера: {ex.Message}");
            }
        }

        /// <summary>
        /// Отправка тестовых данных
        /// </summary>
        private async Task SendTestData()
        {
            if (_networkStream == null) return;

            try
            {
                // Тест 1: Отправляем простые данные
                OnStatusChanged("Отправка тестовых данных...");
                
                string testMessage = "HELLO";
                byte[] testData = Encoding.UTF8.GetBytes(testMessage);
                await _networkStream.WriteAsync(testData, 0, testData.Length);
                OnStatusChanged($"Отправлено: {testMessage}");

                await Task.Delay(500);

                // Тест 2: Отправляем структурированные данные
                await SendStructuredData();
            }
            catch (Exception ex)
            {
                OnErrorOccurred($"Ошибка отправки тестовых данных: {ex.Message}");
            }
        }

        /// <summary>
        /// Отправка структурированных данных (возможный формат протокола)
        /// </summary>
        private async Task SendStructuredData()
        {
            if (_networkStream == null) return;

            try
            {
                // Создаем тестовое изображение 320x240 RGB24
                int width = 320;
                int height = 240;
                
                using (var bitmap = new Bitmap(width, height, PixelFormat.Format24bppRgb))
                {
                    // Заполняем красным цветом
                    using (var graphics = Graphics.FromImage(bitmap))
                    {
                        graphics.Clear(Color.Red);
                        graphics.DrawString("TEST", new Font("Arial", 20), Brushes.White, 10, 10);
                    }

                    // Конвертируем в байты RGB24
                    byte[] imageData = ConvertBitmapToRGB24(bitmap);
                    
                    // Вариант 1: Отправляем только данные изображения
                    OnStatusChanged($"Отправка изображения {width}x{height}, {imageData.Length} байт");
                    await _networkStream.WriteAsync(imageData, 0, imageData.Length);
                    
                    await Task.Delay(500);
                    
                    // Вариант 2: Отправляем с заголовком (размер + данные)
                    var header = new byte[8];
                    BitConverter.GetBytes(width).CopyTo(header, 0);
                    BitConverter.GetBytes(height).CopyTo(header, 4);
                    
                    OnStatusChanged("Отправка с заголовком");
                    await _networkStream.WriteAsync(header, 0, header.Length);
                    await _networkStream.WriteAsync(imageData, 0, imageData.Length);
                }
            }
            catch (Exception ex)
            {
                OnErrorOccurred($"Ошибка отправки структурированных данных: {ex.Message}");
            }
        }

        /// <summary>
        /// Конвертация Bitmap в RGB24 байты
        /// </summary>
        private byte[] ConvertBitmapToRGB24(Bitmap bitmap)
        {
            var rect = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
            var bitmapData = bitmap.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
            
            try
            {
                int stride = Math.Abs(bitmapData.Stride);
                int dataSize = stride * bitmap.Height;
                byte[] data = new byte[dataSize];
                
                System.Runtime.InteropServices.Marshal.Copy(bitmapData.Scan0, data, 0, dataSize);
                return data;
            }
            finally
            {
                bitmap.UnlockBits(bitmapData);
            }
        }

        /// <summary>
        /// Отправка JPEG изображения (простой вариант)
        /// </summary>
        public async Task<bool> SendJpegImageAsync(byte[] jpegData)
        {
            if (!_isConnected || _networkStream == null)
            {
                OnErrorOccurred("Не подключен к серверу");
                return false;
            }

            try
            {
                Console.WriteLine($"[DEBUG] {DateTime.Now:HH:mm:ss.fff} ===== ОТПРАВКА JPEG =====");
                Console.WriteLine($"[DEBUG] Размер JPEG: {jpegData.Length} байт");
                
                // Показываем первые байты JPEG (должны быть FF D8)
                Console.Write("[DEBUG] Первые байты: ");
                for (int i = 0; i < Math.Min(jpegData.Length, 16); i++)
                {
                    Console.Write($"{jpegData[i]:X2} ");
                }
                Console.WriteLine();
                Console.WriteLine("[DEBUG] ========================");
                
                // Простая отправка JPEG данных
                await _networkStream.WriteAsync(jpegData, 0, jpegData.Length);
                OnStatusChanged($"Отправлено JPEG изображение: {jpegData.Length} байт");
                return true;
            }
            catch (Exception ex)
            {
                OnErrorOccurred($"Ошибка отправки JPEG: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Логирование полученных данных
        /// </summary>
        private void LogDataReceived(byte[] data, int length)
        {
            Console.WriteLine($"[DEBUG] {DateTime.Now:HH:mm:ss.fff} ===== ДАННЫЕ ОТ СЕРВЕРА =====");
            Console.WriteLine($"[DEBUG] Получено {length} байт:");
            
            // Hex dump - выводим больше данных
            Console.Write("[DEBUG] HEX: ");
            for (int i = 0; i < Math.Min(length, 64); i++)
            {
                Console.Write($"{data[i]:X2} ");
                if ((i + 1) % 16 == 0) Console.Write("\n[DEBUG]      ");
            }
            if (length > 64) Console.Write("...");
            Console.WriteLine();
            
            // ASCII
            Console.Write("[DEBUG] ASCII: ");
            for (int i = 0; i < Math.Min(length, 64); i++)
            {
                char c = (char)data[i];
                Console.Write(char.IsControl(c) ? '.' : c);
            }
            if (length > 64) Console.Write("...");
            Console.WriteLine();
            Console.WriteLine("[DEBUG] =============================");
            
            // Краткая версия для UI
            var sb = new StringBuilder();
            sb.AppendLine($"Получено {length} байт:");
            
            // Hex dump (первые 16 байт для UI)
            sb.Append("HEX: ");
            for (int i = 0; i < Math.Min(length, 16); i++)
            {
                sb.Append($"{data[i]:X2} ");
            }
            if (length > 16) sb.Append("...");
            sb.AppendLine();
            
            // ASCII (первые 16 байт для UI)
            sb.Append("ASCII: ");
            for (int i = 0; i < Math.Min(length, 16); i++)
            {
                char c = (char)data[i];
                sb.Append(char.IsControl(c) ? '.' : c);
            }
            if (length > 16) sb.Append("...");
            
            OnStatusChanged(sb.ToString());
        }

        /// <summary>
        /// Отключение от сервера
        /// </summary>
        public async Task DisconnectAsync()
        {
            try
            {
                _isConnected = false;
                
                _networkStream?.Close();
                _tcpClient?.Close();
                
                OnStatusChanged("Отключен от сервера");
            }
            catch (Exception ex)
            {
                OnErrorOccurred($"Ошибка отключения: {ex.Message}");
            }
        }

        protected virtual void OnStatusChanged(string status)
        {
            Console.WriteLine($"[DEBUG] {DateTime.Now:HH:mm:ss.fff} STATUS: {status}");
            StatusChanged?.Invoke(this, status);
        }

        protected virtual void OnErrorOccurred(string error)
        {
            Console.WriteLine($"[DEBUG] {DateTime.Now:HH:mm:ss.fff} ERROR: {error}");
            ErrorOccurred?.Invoke(this, error);
        }

        public void Dispose()
        {
            DisconnectAsync().Wait();
            _networkStream?.Dispose();
            _tcpClient?.Dispose();
        }
    }
} 