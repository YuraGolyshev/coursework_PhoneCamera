using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Diagnostics;
using System.Text;
using QRCoder;
using System.Windows.Media;

namespace CameraReceiver
{
    public partial class MainWindow : Window
    {
        private TcpListener? _server;
        private CancellationTokenSource? _cancellationTokenSource;
        private readonly int _port = 8888;
        private bool _isServerRunning = false;

        // Очень простой протокол с уникальными маркерами
        private static readonly byte[] FRAME_START_MARKER = new byte[] { 0x01, 0x02, 0x03, 0x04 }; // Явно отличимый маркер начала кадра
        private static readonly byte[] FRAME_END_MARKER = new byte[] { 0x04, 0x03, 0x02, 0x01 }; // Явно отличимый маркер конца кадра

        // Для чтения данных побайтово (диагностика)
        private StringBuilder _dataBuffer = new StringBuilder();

        // Ограничение обновления UI до 60 FPS
        private long _lastUiFrameTicks = 0;
        private readonly long _uiFrameIntervalTicks = Stopwatch.Frequency / 60; // ~16мс

        private Window? _qrWindow;

        // Поля для виртуальной камеры
        private readonly VirtualCameraManager _virtualCameraManager;
        private byte[]? _lastJpegData; // Последние полученные JPEG данные

        public MainWindow()
        {
            InitializeComponent();
            
            // Инициализация виртуальной камеры
            _virtualCameraManager = new VirtualCameraManager();
            _virtualCameraManager.StatusChanged += OnVirtualCameraStatusChanged;
            _virtualCameraManager.ErrorOccurred += OnVirtualCameraError;
        }

        private async void StartButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                StartButton.IsEnabled = false;
                StopButton.IsEnabled = true;
                _isServerRunning = true;

                // Проверяем и создаем правило брандмауэра при первом запуске
                await EnsureFirewallRuleAsync();

                _cancellationTokenSource = new CancellationTokenSource();
                await StartServer(_cancellationTokenSource.Token);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Ошибка при запуске сервера: {ex.Message}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
                StopServer();
            }
        }

        private void StopButton_Click(object sender, RoutedEventArgs e)
        {
            StopServer();
        }

        private void StopServer()
        {
            _isServerRunning = false;
            _cancellationTokenSource?.Cancel();
            _server?.Stop();
            
            Dispatcher.Invoke(() => 
            {
                StatusText.Text = "Сервер остановлен";
                StartButton.IsEnabled = true;
                StopButton.IsEnabled = false;
            });
        }

        private async Task StartServer(CancellationToken cancellationToken)
        {
            try
            {
                string localIp = GetLocalIPAddress();
                
                bool isPortInUse = IsPortInUse(localIp, _port);
                if (isPortInUse)
                {
                    Dispatcher.Invoke(() =>
                    {
                        StatusText.Text = $"Порт {_port} на {localIp} уже используется";
                        MessageBox.Show($"Порт {_port} на IP {localIp} уже используется другим приложением!\n\nВозможные решения:\n1. Закройте другое приложение, использующее порт\n2. Измените порт в настройках приложения\n3. Перезапустите компьютер", 
                            "Ошибка сети", MessageBoxButton.OK, MessageBoxImage.Error);
                    });
                    return;
                }

                _server = new TcpListener(IPAddress.Parse(localIp), _port);
                try
                {
                    _server.Start();
                }
                catch (SocketException ex)
                {
                    Dispatcher.Invoke(() =>
                    {
                        StatusText.Text = $"Ошибка при запуске сервера: {ex.Message} (код {ex.ErrorCode})";
                        MessageBox.Show($"Невозможно запустить сервер на {localIp}:{_port}\nОшибка: {ex.Message}\nКод ошибки: {ex.ErrorCode}\n\nПричины могут быть следующие:\n1. Недостаточно прав (запустите приложение от имени администратора)\n2. Порт {_port} блокируется брандмауэром\n3. Другое приложение использует этот порт\n4. IP адрес {localIp} недоступен", 
                            "Ошибка сети", MessageBoxButton.OK, MessageBoxImage.Error);
                    });
                    return;
                }
                Dispatcher.Invoke(() => StatusText.Text = $"Сервер запущен на {localIp}:{_port}");
                
                MessageBox.Show($"Сервер запущен и слушает подключения на конкретном IP адресе.\n\nIP: {localIp}\nПорт: {_port}\n\nЕсли возникают проблемы с подключением:\n1. Проверьте, что брандмауэр разрешает входящие подключения\n2. Проверьте, что устройства находятся в одной сети\n3. Убедитесь, что телефон подключается именно к этому IP", 
                    "Информация о сервере", MessageBoxButton.OK, MessageBoxImage.Information);

                while (!cancellationToken.IsCancellationRequested)
                {
                    Dispatcher.Invoke(() => StatusText.Text = $"Ожидание подключения на {localIp}:{_port}");
                    
                    // Принимаем клиента асинхронно с поддержкой отмены
                    TcpClient client;
                    try
                    {
                        client = await _server.AcceptTcpClientAsync().WithCancellation(cancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }

                    // Настройка клиента для более надёжной работы
                    client.NoDelay = true; // Отключаем алгоритм Нейгла для быстрой передачи данных
                    client.ReceiveBufferSize = 1_048_576; // 1 МБ буфер приёма
                    client.SendBufferSize = 1_048_576; // 1 МБ буфер отправки
                    client.ReceiveTimeout = 10000; // 10 секунд таймаут на чтение
                    client.SendTimeout = 5000; // 5 секунд таймаут на запись

                    // Обрабатываем полученные данные
                    var endpoint = (IPEndPoint)client.Client.RemoteEndPoint;
                    string clientInfo = $"{endpoint.Address}:{endpoint.Port}";
                    Dispatcher.Invoke(() => StatusText.Text = $"Клиент подключен: {clientInfo}");
                    
                    // Запускаем ускоренную обработку данных
                    _ = Task.Run(() => ProcessClientFast(client, cancellationToken), cancellationToken);
                }
            }
            catch (SocketException ex)
            {
                Dispatcher.Invoke(() =>
                {
                    StatusText.Text = $"Ошибка сокета: {ex.Message} (код {ex.ErrorCode})";
                    MessageBox.Show($"Ошибка сокета: {ex.Message}\nКод ошибки: {ex.ErrorCode}\n\nВозможно, порт {_port} уже используется другим приложением или не доступен.", 
                        "Ошибка сети", MessageBoxButton.OK, MessageBoxImage.Error);
                });
            }
            catch (OperationCanceledException)
            {
                // Нормальное завершение при отмене
            }
            catch (Exception ex)
            {
                Dispatcher.Invoke(() =>
                {
                    StatusText.Text = $"Ошибка: {ex.Message}";
                    MessageBox.Show($"Ошибка сервера: {ex.Message}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
                });
            }
            finally
            {
                _server?.Stop();
            }
        }

        // Дополнительный диагностический метод для чтения байт за байтом
        private async Task ProcessClientDiagnostic(TcpClient client, CancellationToken cancellationToken)
        {
            try
            {
                using (client)
                using (NetworkStream stream = client.GetStream())
                {
                    var endpoint = (IPEndPoint)client.Client.RemoteEndPoint;
                    string clientInfo = $"{endpoint.Address}:{endpoint.Port}";
                    Dispatcher.Invoke(() => StatusText.Text = $"Обработка данных от {clientInfo}");
                    
                    byte[] buffer = new byte[1]; // Читаем по одному байту
                    int bytesRead;
                    int bytesCounter = 0;
                    
                    Stopwatch totalTimer = Stopwatch.StartNew();
                    
                    // Буферы для хранения байт и состояний
                    byte[] startMarkerBuffer = new byte[FRAME_START_MARKER.Length];
                    int startMarkerPos = 0;
                    byte[] endMarkerBuffer = new byte[FRAME_END_MARKER.Length];
                    int endMarkerPos = 0;
                    
                    bool inFrame = false; // Флаг нахождения внутри кадра
                    bool readingHeader = false; // Чтение заголовка размера
                    byte[] sizeBuffer = new byte[4];
                    int sizePos = 0;
                    byte[] frameData = null;
                    int framePos = 0;
                    
                    _dataBuffer.Clear();
                    
                    while (!cancellationToken.IsCancellationRequested && client.Connected)
                    {
                        try
                        {
                            bytesRead = await stream.ReadAsync(buffer, 0, 1, cancellationToken);
                            if (bytesRead <= 0)
                            {
                                Dispatcher.Invoke(() => StatusText.Text = $"Соединение закрыто клиентом");
                                break;
                            }
                            
                            bytesCounter++;
                            
                            // Выводим первые 100 байт для диагностики
                            if (bytesCounter <= 100)
                            {
                                _dataBuffer.Append($"{buffer[0]:X2} ");
                                if (bytesCounter % 20 == 0)
                                {
                                    Dispatcher.Invoke(() => StatusText.Text = $"Прочитано первые {bytesCounter} байт: {_dataBuffer}");
                                }
                            }
                            
                            // Отображаем информацию о каждых 1000 байт
                            if (bytesCounter % 1000 == 0)
                            {
                                Dispatcher.Invoke(() => StatusText.Text = $"Прочитано байт: {bytesCounter}, время: {totalTimer.Elapsed.TotalSeconds:F1} с");
                            }
                            
                            // Основная логика обработки кадра
                            if (!inFrame)
                            {
                                // Ищем маркер начала
                                if (buffer[0] == FRAME_START_MARKER[startMarkerPos])
                                {
                                    startMarkerBuffer[startMarkerPos] = buffer[0];
                                    startMarkerPos++;
                                    
                                    // Нашли полный маркер начала?
                                    if (startMarkerPos == FRAME_START_MARKER.Length)
                                    {
                                        Dispatcher.Invoke(() => StatusText.Text = $"Найден маркер начала кадра!");
                                        inFrame = true;
                                        readingHeader = true;
                                        startMarkerPos = 0;
                                    }
                                }
                                else
                                {
                                    // Если это первый байт маркера, начинаем с него
                                    if (buffer[0] == FRAME_START_MARKER[0])
                                    {
                                        startMarkerBuffer[0] = buffer[0];
                                        startMarkerPos = 1;
                                    }
                                    else
                                    {
                                        startMarkerPos = 0;
                                    }
                                }
                            }
                            else if (readingHeader)
                            {
                                // Читаем заголовок (размер кадра)
                                sizeBuffer[sizePos++] = buffer[0];
                                
                                if (sizePos == 4) // Прочитали 4 байта размера
                                {
                                    int frameSize = BitConverter.ToInt32(sizeBuffer, 0);
                                    
                                    if (frameSize <= 0 || frameSize > 10 * 1024 * 1024) // Проверка разумных пределов
                                    {
                                        Dispatcher.Invoke(() => StatusText.Text = $"Некорректный размер кадра: {frameSize} байт");
                                        inFrame = false;
                                        readingHeader = false;
                                        sizePos = 0;
                                    }
                                    else
                                    {
                                        Dispatcher.Invoke(() => StatusText.Text = $"Чтение кадра размером {frameSize} байт...");
                                        frameData = new byte[frameSize];
                                        framePos = 0;
                                        readingHeader = false;
                                    }
                                }
                            }
                            else if (frameData != null && framePos < frameData.Length)
                            {
                                // Читаем данные кадра
                                frameData[framePos++] = buffer[0];
                                
                                // Вывод прогресса для больших кадров
                                if (frameData.Length > 10000 && framePos % 10000 == 0)
                                {
                                    int percent = (framePos * 100) / frameData.Length;
                                    Dispatcher.Invoke(() => StatusText.Text = $"Прогресс чтения кадра: {percent}% ({framePos}/{frameData.Length} байт)");
                                }
                                
                                // Прочитали все данные кадра?
                                if (framePos == frameData.Length)
                                {
                                    Dispatcher.Invoke(() => StatusText.Text = $"Кадр прочитан, ожидание маркера конца...");
                                    
                                    // Теперь ищем маркер конца
                                    endMarkerPos = 0;
                                }
                            }
                            else
                            {
                                // Ищем маркер конца кадра
                                if (buffer[0] == FRAME_END_MARKER[endMarkerPos])
                                {
                                    endMarkerBuffer[endMarkerPos] = buffer[0];
                                    endMarkerPos++;
                                    
                                    if (endMarkerPos == FRAME_END_MARKER.Length)
                                    {
                                        Dispatcher.Invoke(() => StatusText.Text = $"Найден маркер конца кадра!");
                                        
                                        // Обработка полученного кадра
                                        if (frameData != null)
                                        {
                                            // Попробуем определить, тестовый это пакет или изображение
                                            if (frameData.Length < 100)
                                            {
                                                try
                                                {
                                                    string testStr = Encoding.UTF8.GetString(frameData);
                                                    if (testStr == "CAMERA_TEST_PACKET")
                                                    {
                                                        Dispatcher.Invoke(() => StatusText.Text = $"Получен тестовый пакет");
                                                    }
                                                    else
                                                    {
                                                        Dispatcher.Invoke(() => StatusText.Text = $"Получен неизвестный текстовый пакет: {testStr}");
                                                    }
                                                }
                                                catch
                                                {
                                                    Dispatcher.Invoke(() => StatusText.Text = $"Получен бинарный пакет размером {frameData.Length} байт");
                                                }
                                            }
                                            else
                                            {
                                                // Сохраняем последние JPEG данные
                                                _lastJpegData = frameData;

                                                // Получаем значение чекбокса в UI потоке
                                                bool enableVCam = true; //bool enableVCam = EnableVCamCheckBox.IsChecked == true;

                                                // Отправка в виртуальную камеру (асинхронно, чтобы не блокировать основной поток)
                                                if (enableVCam && _virtualCameraManager.IsStarted)
                                                {
                                                    _ = Task.Run(async () =>
                                                    {
                                                        try
                                                        {
                                                            await _virtualCameraManager.SendFrameAsync(frameData);
                                                        }
                                                        catch (Exception ex)
                                                        {
                                                            Dispatcher.Invoke(() => VCamStatusText.Text = $"VCam: Ошибка отправки - {ex.Message}");
                                                        }
                                                    });
                                                }

                                                // Декодирование в пуле ThreadPool, UI обновляем не чаще 60 Гц
                                                _ = Task.Run(() =>
                                                {
                                                    try
                                                    {
                                                        BitmapImage bmp;
                                                        using (var ms = new MemoryStream(frameData))
                                                        {
                                                            bmp = new BitmapImage();
                                                            bmp.BeginInit();
                                                            bmp.CacheOption = BitmapCacheOption.OnLoad;
                                                            bmp.StreamSource = ms;
                                                            bmp.EndInit();
                                                            bmp.Freeze();
                                                        }

                                                        var nowTicks = Stopwatch.GetTimestamp();
                                                        if (nowTicks - _lastUiFrameTicks >= _uiFrameIntervalTicks)
                                                        {
                                                            _lastUiFrameTicks = nowTicks;
                                                            Dispatcher.Invoke(() =>
                                                            {
                                                                CameraImage.Source = bmp;
                                                                StatusText.Text = $"Кадр: {frameData.Length / 1024} KB";
                                                                ResolutionText.Text = $"{bmp.PixelWidth}×{bmp.PixelHeight}";
                                                            });
                                                        }
                                                    }
                                                    catch (Exception ex)
                                                    {
                                                        Dispatcher.Invoke(() => StatusText.Text = $"Ошибка изображения: {ex.Message}");
                                                    }
                                                });
                                            }
                                        }
                                        
                                        // Сбрасываем состояние для поиска нового кадра
                                        inFrame = false;
                                        frameData = null;
                                        framePos = 0;
                                        endMarkerPos = 0;
                                        sizePos = 0;
                                    }
                                }
                                else
                                {
                                    // Если это первый байт конечного маркера, начинаем с него
                                    if (buffer[0] == FRAME_END_MARKER[0])
                                    {
                                        endMarkerBuffer[0] = buffer[0];
                                        endMarkerPos = 1;
                                    }
                                    else
                                    {
                                        endMarkerPos = 0;
                                    }
                                }
                            }
                            
                        }
                        catch (OperationCanceledException)
                        {
                            if (cancellationToken.IsCancellationRequested)
                                break;
                                
                            Dispatcher.Invoke(() => StatusText.Text = $"Таймаут чтения");
                        }
                        catch (Exception ex)
                        {
                            Dispatcher.Invoke(() => StatusText.Text = $"Ошибка при чтении данных: {ex.Message}");
                            await Task.Delay(1000, cancellationToken);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Dispatcher.Invoke(() => StatusText.Text = $"Ошибка: {ex.Message}");
            }
            finally
            {
                var endpoint = client.Client.RemoteEndPoint as IPEndPoint;
                Dispatcher.Invoke(() => 
                {
                    StatusText.Text = endpoint != null 
                        ? $"Клиент отключен: {endpoint.Address}:{endpoint.Port}" 
                        : "Клиент отключен";
                });
            }
        }

        // ------------------ Быстрая обработка клиента ------------------
        private async Task ProcessClientFast(TcpClient client, CancellationToken cancellationToken)
        {
            try
            {
                using (client)
                using (NetworkStream stream = client.GetStream())
                {
                    var endpoint = (IPEndPoint)client.Client.RemoteEndPoint;
                    Dispatcher.Invoke(() =>
                    {
                        StatusText.Text = $"Fast-режим: {endpoint.Address}:{endpoint.Port}";
                        _qrWindow?.Close();
                    });

                    int framesInSecond = 0;
                    long bytesInSecond = 0;
                    var secWatch = Stopwatch.StartNew();

                    while (!cancellationToken.IsCancellationRequested && client.Connected)
                    {
                        // 1. Маркер начала
                        var startBuf = await ReadExactAsync(stream, FRAME_START_MARKER.Length, cancellationToken);
                        if (!startBuf.AsSpan().SequenceEqual(FRAME_START_MARKER))
                        {
                            // Ищем правильное выравнивание: пропускаем 1 байт и продолжаем
                            continue;
                        }

                        // 2. Размер кадра (4 байта LE)
                        var sizeBuf = await ReadExactAsync(stream, 4, cancellationToken);
                        int frameSize = BitConverter.ToInt32(sizeBuf, 0);
                        if (frameSize <= 0 || frameSize > 10 * 1024 * 1024)
                        {
                            Dispatcher.Invoke(() => StatusText.Text = $"Некорректный размер кадра {frameSize}");
                            break;
                        }

                        // 3. Данные кадра
                        byte[] frameData = await ReadExactAsync(stream, frameSize, cancellationToken);

                        // 4. Маркер конца
                        var endBuf = await ReadExactAsync(stream, FRAME_END_MARKER.Length, cancellationToken);
                        if (!endBuf.AsSpan().SequenceEqual(FRAME_END_MARKER))
                        {
                            Dispatcher.Invoke(() => StatusText.Text = "Маркер конца не совпал");
                            break;
                        }

                        // Сохраняем последние JPEG данные
                        _lastJpegData = frameData;
                        
                        // Получаем значение чекбокса в UI потоке
                        bool enableVCam = false;
                        Dispatcher.Invoke(() => enableVCam = true); //Dispatcher.Invoke(() => enableVCam = EnableVCamCheckBox.IsChecked == true);

                        // Отправка в виртуальную камеру (асинхронно, чтобы не блокировать основной поток)
                        if (enableVCam && _virtualCameraManager.IsStarted)
                        {
                            _ = Task.Run(async () =>
                            {
                                try
                                {
                                    await _virtualCameraManager.SendFrameAsync(frameData);
                                }
                                catch (Exception ex)
                                {
                                    Dispatcher.Invoke(() => VCamStatusText.Text = $"VCam: Ошибка отправки - {ex.Message}");
                                }
                            });
                        }

                        // Декодирование в пуле ThreadPool, UI обновляем не чаще 60 Гц
                        _ = Task.Run(() =>
                        {
                            try
                            {
                                BitmapImage bmp;
                                using (var ms = new MemoryStream(frameData))
                                {
                                    bmp = new BitmapImage();
                                    bmp.BeginInit();
                                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                                    bmp.StreamSource = ms;
                                    bmp.EndInit();
                                    bmp.Freeze();
                                }

                                var nowTicks = Stopwatch.GetTimestamp();
                                if (nowTicks - _lastUiFrameTicks >= _uiFrameIntervalTicks)
                                {
                                    _lastUiFrameTicks = nowTicks;
                                    Dispatcher.Invoke(() =>
                                    {
                                        CameraImage.Source = bmp;
                                        StatusText.Text = $"Кадр: {frameSize / 1024} KB";
                                        ResolutionText.Text = $"{bmp.PixelWidth}×{bmp.PixelHeight}";
                                    });
                                }
                            }
                            catch (Exception ex)
                            {
                                Dispatcher.Invoke(() => StatusText.Text = $"Ошибка изображения: {ex.Message}");
                            }
                        });

                        // статистика
                        framesInSecond++;
                        bytesInSecond += FRAME_START_MARKER.Length + 4 + frameSize + FRAME_END_MARKER.Length;

                        if (secWatch.ElapsedMilliseconds >= 1000)
                        {
                            int fps = framesInSecond;
                            long kb = bytesInSecond / 1024;

                            Dispatcher.Invoke(() =>
                            {
                                FpsCounter.Text = $"{fps} FPS";
                                DataRateText.Text = $"{kb} КБ/с";
                            });

                            framesInSecond = 0;
                            bytesInSecond = 0;
                            secWatch.Restart();
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // нормальное завершение
            }
            catch (Exception ex)
            {
                Dispatcher.Invoke(() => StatusText.Text = $"Fast-ошибка: {ex.Message}");
            }
        }

        // Чтение точного количества байт из потока
        private static async Task<byte[]> ReadExactAsync(NetworkStream stream, int size, CancellationToken token)
        {
            byte[] buf = new byte[size];
            int pos = 0;
            while (pos < size)
            {
                int read = await stream.ReadAsync(buf, pos, size - pos, token);
                if (read == 0)
                    throw new IOException("Соединение разорвано");
                pos += read;
            }
            return buf;
        }
        // ------------------ Конец fast-режима ------------------

        private string GetLocalIPAddress()
        {
            try
            {
                // Пробуем найти активное сетевое соединение
                using (var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, 0))
                {
                    // Подключаемся к внешнему адресу (не отправляем данные)
                    socket.Connect("8.8.8.8", 65530);
                    var endPoint = socket.LocalEndPoint as IPEndPoint;
                    return endPoint?.Address.ToString() ?? "127.0.0.1";
                }
            }
            catch
            {
                // Если не удалось - используем старый метод как резервный
                try
                {
                    var host = Dns.GetHostEntry(Dns.GetHostName());
                    foreach (var ip in host.AddressList)
                    {
                        if (ip.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ip))
                        {
                            return ip.ToString();
                        }
                    }
                }
                catch
                {
                    // Игнорируем ошибки
                }
                return "127.0.0.1";
            }
        }

        // Добавляем новый метод для проверки использования порта на конкретном IP
        private bool IsPortInUse(string ipAddress, int port)
        {
            try
            {
                // Проверяем, не используется ли порт на конкретном IP, создав временный TCP-слушатель
                using (var tempListener = new TcpListener(IPAddress.Parse(ipAddress), port))
                {
                    tempListener.Start();
                    tempListener.Stop();
                    return false; // Порт свободен
                }
            }
            catch
            {
                return true; // Порт занят или недоступен
            }
        }

        private void QrButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string localIp = GetLocalIPAddress();
                string text = $"cam://{localIp}:{_port}";

                using var gen = new QRCodeGenerator();
                using var data = gen.CreateQrCode(text, QRCodeGenerator.ECCLevel.Q);
                var pngBytes = new PngByteQRCode(data).GetGraphic(5);

                using var ms = new MemoryStream(pngBytes);
                BitmapImage bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.StreamSource = ms;
                bmp.EndInit();
                bmp.Freeze();

                if (_qrWindow == null || !_qrWindow.IsVisible)
                {
                    _qrWindow = new Window
                    {
                        Title = "QR для подключения",
                        Width = 350,
                        Height = 350,
                        Content = new System.Windows.Controls.Image { Source = bmp, Stretch = Stretch.Uniform }
                    };
                    _qrWindow.Closed += (_, __) => _qrWindow = null;
                    _qrWindow.Show();
                }
                else if (_qrWindow.Content is System.Windows.Controls.Image img)
                {
                    img.Source = bmp;
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Ошибка генерации QR: {ex.Message}", "QR", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // ==================== ОБРАБОТЧИКИ ВИРТУАЛЬНОЙ КАМЕРЫ ====================

        private async void RegisterVCamButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                RegisterVCamButton.IsEnabled = false;
                VCamStatusText.Text = "VCam: Регистрация...";

                // Путь к DLL файлу виртуальной камеры
                string dllPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "vCam", "VirtualCamFilter.dll");
                
                if (!File.Exists(dllPath))
                {
                    VCamStatusText.Text = "VCam: DLL файл не найден";
                    MessageBox.Show($"DLL файл не найден по пути:\n{dllPath}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                // Команда регистрации: regsvr32 /s "путь_к_dll"
                var processInfo = new ProcessStartInfo
                {
                    FileName = "regsvr32",
                    Arguments = $"/s \"{dllPath}\"", // /s = silent mode (без диалогов)
                    UseShellExecute = true,
                    Verb = "runas", // Запуск от имени администратора
                    CreateNoWindow = true
                };

                using var process = Process.Start(processInfo);
                await Task.Run(() => process?.WaitForExit());

                if (process?.ExitCode == 0)
                {
                    VCamStatusText.Text = "VCam: Успешно зарегистрирована";
                    StatusText.Text = "Виртуальная камера зарегистрирована в системе";
                    MessageBox.Show("Виртуальная камера успешно зарегистрирована!\nТеперь она доступна в приложениях.", "Успех", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else
                {
                    VCamStatusText.Text = "VCam: Ошибка регистрации";
                    MessageBox.Show("Ошибка при регистрации виртуальной камеры.\nПроверьте права администратора.", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            catch (Exception ex)
            {
                VCamStatusText.Text = "VCam: Ошибка регистрации";
                MessageBox.Show($"Ошибка при регистрации виртуальной камеры:\n{ex.Message}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                RegisterVCamButton.IsEnabled = true;
            }
        }

        private async void UnregisterVCamButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // Подтверждение удаления
                var result = MessageBox.Show("Вы уверены, что хотите удалить виртуальную камеру из системы?", 
                    "Подтверждение", MessageBoxButton.YesNo, MessageBoxImage.Question);
                
                if (result != MessageBoxResult.Yes)
                    return;

                UnregisterVCamButton.IsEnabled = false;
                VCamStatusText.Text = "VCam: Удаление...";

                // Путь к DLL файлу виртуальной камеры
                string dllPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "vCam", "VirtualCamFilter.dll");
                
                if (!File.Exists(dllPath))
                {
                    VCamStatusText.Text = "VCam: DLL файл не найден";
                    MessageBox.Show($"DLL файл не найден по пути:\n{dllPath}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                // Команда удаления: regsvr32 /u /s "путь_к_dll"
                var processInfo = new ProcessStartInfo
                {
                    FileName = "regsvr32",
                    Arguments = $"/u /s \"{dllPath}\"", // /u = unregister, /s = silent mode
                    UseShellExecute = true,
                    Verb = "runas", // Запуск от имени администратора
                    CreateNoWindow = true
                };

                using var process = Process.Start(processInfo);
                await Task.Run(() => process?.WaitForExit());

                if (process?.ExitCode == 0)
                {
                    VCamStatusText.Text = "VCam: Успешно удалена";
                    StatusText.Text = "Виртуальная камера удалена из системы";
                    MessageBox.Show("Виртуальная камера успешно удалена из системы.", "Успех", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else
                {
                    VCamStatusText.Text = "VCam: Ошибка удаления";
                    MessageBox.Show("Ошибка при удалении виртуальной камеры.\nПроверьте права администратора.", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            catch (Exception ex)
            {
                VCamStatusText.Text = "VCam: Ошибка удаления";
                MessageBox.Show($"Ошибка при удалении виртуальной камеры:\n{ex.Message}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                UnregisterVCamButton.IsEnabled = true;
            }
        }

        //private async void InitVCamButton_Click(object sender, RoutedEventArgs e)
        //{
        //    //InitVCamButton.IsEnabled = false;
            
        //    try
        //    {
        //        var success = await _virtualCameraManager.InitializeAsync("CameraReceiver");
        //        if (success)
        //        {
        //            StartVCamButton.IsEnabled = true;
        //            VCamStatusText.Text = "VCam: Готов к подключению";
        //        }
        //        else
        //        {
        //            VCamStatusText.Text = "VCam: Ошибка инициализации";
        //        }
        //    }
        //    finally
        //    {
        //        if (!_virtualCameraManager.IsInitialized)
        //        {
        //            //InitVCamButton.IsEnabled = true;
        //        }
        //    }
        //}

        private async void StartVCamButton_Click(object sender, RoutedEventArgs e)
        {
            //StartVCamButton.IsEnabled = false;

            try
            {
                if (_virtualCameraManager.IsStarted)
                {
                    // Отключаемся
                    await _virtualCameraManager.StopAsync();
                    StartVCamButton.Content = "Передать изображение на виртуальную камеру";
                    //EnableVCamCheckBox.IsChecked = false;
                    VCamStatusText.Text = "VCam: Отключен";
                }
                else
                {
                    //InitVCamButton_Click()
                    var success_InitVCamButton = await _virtualCameraManager.InitializeAsync("CameraReceiver");
                    if (success_InitVCamButton)
                    {
                       StartVCamButton.IsEnabled = true;
                       VCamStatusText.Text = "VCam: Готов к подключению";
                    }
                    else
                    {
                        VCamStatusText.Text = "VCam: Ошибка инициализации";
                    }
                    // Подключаемся
                    var success = await _virtualCameraManager.StartAsync();
                    if (success)
                    {
                        StartVCamButton.Content = "Остановить переду изображения";
                        VCamStatusText.Text = "VCam: Подключен";
                    }
                    else
                    {
                        VCamStatusText.Text = "VCam: Ошибка подключения";
                    }
                }
            }
            finally
            {
                StartVCamButton.IsEnabled = true;
            }
        }

        private void OnVirtualCameraStatusChanged(object? sender, string status)
        {
            Dispatcher.Invoke(() =>
            {
                VCamStatusText.Text = $"VCam: {status}";
            });
        }

        private void OnVirtualCameraError(object? sender, string error)
        {
            Dispatcher.Invoke(() =>
            {
                VCamStatusText.Text = $"VCam: Ошибка - {error}";
                StatusText.Text = $"Ошибка виртуальной камеры: {error}";
            });
        }

        // ==================== МЕТОДЫ РАБОТЫ С БРАНДМАУЭРОМ ====================

        /// <summary>
        /// Проверяет существование правила брандмауэра и создает его при необходимости
        /// </summary>
        private async Task EnsureFirewallRuleAsync()
        {
            try
            {
                string ruleName = $"CameraReceiver Port {_port}";
                
                // Проверяем, существует ли уже правило
                bool ruleExists = await CheckFirewallRuleExistsAsync(ruleName);
                
                if (!ruleExists)
                {
                    StatusText.Text = "Создание правила брандмауэра...";
                    
                    // Спрашиваем разрешение у пользователя
                    var result = MessageBox.Show(
                        $"Для работы приложения необходимо создать правило в брандмауэре Windows для порта {_port}.\n\n" +
                        "Это разрешит подключение к приложению с других устройств в сети.\n\n" +
                        "Создать правило брандмауэра?",
                        "Настройка брандмауэра",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Question);
                    
                    if (result == MessageBoxResult.Yes)
                    {
                        bool created = await CreateFirewallRuleAsync(ruleName);
                        
                        if (created)
                        {
                            StatusText.Text = "Правило брандмауэра создано успешно";
                            MessageBox.Show($"Правило брандмауэра для порта {_port} успешно создано!", 
                                "Успех", MessageBoxButton.OK, MessageBoxImage.Information);
                        }
                        else
                        {
                            StatusText.Text = "Ошибка создания правила брандмауэра";
                            MessageBox.Show("Не удалось создать правило брандмауэра.\n" +
                                "Возможно, требуются права администратора.\n\n" +
                                "Вы можете создать правило вручную:\n" +
                                $"1. Откройте 'Брандмауэр Windows с расширенной безопасностью'\n" +
                                $"2. Создайте правило для входящих подключений\n" +
                                $"3. Разрешите TCP подключения на порт {_port}",
                                "Предупреждение", MessageBoxButton.OK, MessageBoxImage.Warning);
                        }
                    }
                    else
                    {
                        StatusText.Text = "Правило брандмауэра не создано";
                        MessageBox.Show("Правило брандмауэра не создано.\n" +
                            "Подключение с других устройств может не работать.\n\n" +
                            "При необходимости создайте правило вручную в настройках брандмауэра Windows.",
                            "Информация", MessageBoxButton.OK, MessageBoxImage.Information);
                    }
                }
                else
                {
                    StatusText.Text = "Правило брандмауэра уже существует";
                }
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Ошибка проверки брандмауэра: {ex.Message}";
                MessageBox.Show($"Ошибка при работе с брандмауэром: {ex.Message}\n\n" +
                    "Сервер будет запущен, но подключение с других устройств может не работать.",
                    "Предупреждение", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        /// <summary>
        /// Проверяет, существует ли правило брандмауэра с указанным именем
        /// </summary>
        private async Task<bool> CheckFirewallRuleExistsAsync(string ruleName)
        {
            try
            {
                var processInfo = new ProcessStartInfo
                {
                    FileName = "netsh",
                    Arguments = $"advfirewall firewall show rule name=\"{ruleName}\"",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.GetEncoding(866) // CP866 для русской Windows
                };

                using var process = Process.Start(processInfo);
                if (process == null) return false;

                string output = await process.StandardOutput.ReadToEndAsync();
                await Task.Run(() => process.WaitForExit());

                // Если правило существует, в выводе будет содержаться имя правила
                return process.ExitCode == 0 && output.Contains(ruleName);
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Ошибка проверки правила: {ex.Message}";
                return false;
            }
        }

        /// <summary>
        /// Создает правило брандмауэра для разрешения входящих подключений на указанный порт
        /// </summary>
        private async Task<bool> CreateFirewallRuleAsync(string ruleName)
        {
            try
            {
                var processInfo = new ProcessStartInfo
                {
                    FileName = "netsh",
                    Arguments = $"advfirewall firewall add rule name=\"{ruleName}\" dir=in action=allow protocol=TCP localport={_port}",
                    UseShellExecute = true,
                    Verb = "runas", // Запуск от имени администратора
                    CreateNoWindow = true
                };

                using var process = Process.Start(processInfo);
                if (process == null) return false;

                await Task.Run(() => process.WaitForExit());

                return process.ExitCode == 0;
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Ошибка создания правила: {ex.Message}";
                return false;
            }
        }

        protected override void OnClosed(EventArgs e)
        {
            _virtualCameraManager?.Dispose();
            base.OnClosed(e);
        }
    }

    // Расширение для поддержки отмены в AcceptTcpClientAsync
    public static class TcpListenerExtensions
    {
        public static async Task<TcpClient> WithCancellation(this Task<TcpClient> task, CancellationToken cancellationToken)
        {
            var tcs = new TaskCompletionSource<bool>();
            using (cancellationToken.Register(s => ((TaskCompletionSource<bool>)s).TrySetResult(true), tcs))
            {
                if (task != await Task.WhenAny(task, tcs.Task))
                {
                    throw new OperationCanceledException(cancellationToken);
                }
            }
            return await task;
        }
    }
}