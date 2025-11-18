using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace vCam
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        private readonly ImageGenerator _imageGenerator;
        private readonly VirtualCameraManager _virtualCameraManager;
        private VirtualCameraDebugClient? _debugClient;
        private DispatcherTimer? _timer;
        private int _frameCount;
        private bool _isRunning;

        public MainWindow()
        {
            InitializeComponent();

            // Тестируем консольный вывод
            Console.WriteLine("=== vCam ЗАПУЩЕН ===");
            Console.WriteLine($"Время запуска: {DateTime.Now:HH:mm:ss.fff}");
            Console.WriteLine("Консоль работает! Нажмите кнопку отладки для тестирования.");
            Console.WriteLine("=====================================");

            _imageGenerator = new ImageGenerator();
            _virtualCameraManager = new VirtualCameraManager();
            _frameCount = 0;
            _isRunning = false;
            
            // Подписываемся на события виртуальной камеры
            _virtualCameraManager.StatusChanged += OnVirtualCameraStatusChanged;
            _virtualCameraManager.ErrorOccurred += OnVirtualCameraError;
            
            // Генерируем первый кадр при запуске
            GenerateAndDisplayFrame();
        }

        private void StartButton_Click(object sender, RoutedEventArgs e)
        {
            Console.WriteLine($"[UI] {DateTime.Now:HH:mm:ss.fff} StartButton_Click: Начинаем генерацию кадров");
            
            if (_isRunning) 
            {
                Console.WriteLine($"[UI] Генерация уже запущена");
                return;
            }

            // Получаем выбранный FPS
            var fpsText = ((System.Windows.Controls.ComboBoxItem)FpsComboBox.SelectedItem).Content.ToString();
            if (int.TryParse(fpsText, out int fps))
            {
                Console.WriteLine($"[UI] Запускаем генерацию с FPS: {fps}");
                Console.WriteLine($"[UI] VCam статус - Инициализирован: {_virtualCameraManager.IsInitialized}, Подключен: {_virtualCameraManager.IsStarted}");
                
                // Создаем и запускаем таймер
                _timer = new DispatcherTimer();
                _timer.Interval = TimeSpan.FromMilliseconds(1000.0 / fps);
                _timer.Tick += Timer_Tick;
                _timer.Start();

                _isRunning = true;
                StartButton.IsEnabled = false;
                StopButton.IsEnabled = true;
                StatusText.Text = $"Запущено (FPS: {fps})";
            }
        }

        private void StopButton_Click(object sender, RoutedEventArgs e)
        {
            StopGeneration();
        }

        private void SingleFrameButton_Click(object sender, RoutedEventArgs e)
        {
            GenerateAndDisplayFrame();
        }

        private void Timer_Tick(object? sender, EventArgs e)
        {
            GenerateAndDisplayFrame();
        }

        private void StopGeneration()
        {
            if (_timer != null)
            {
                _timer.Stop();
                _timer = null;
            }

            _isRunning = false;
            StartButton.IsEnabled = true;
            StopButton.IsEnabled = false;
            StatusText.Text = "Остановлено";
        }

        private void GenerateAndDisplayFrame()
        {
            try
            {
                // Генерируем изображение в правильном разрешении
                int width = 640, height = 480;
                if (_virtualCameraManager.IsStarted)
                {
                    // Получаем размеры напрямую из TCP клиента
                    width = _virtualCameraManager.GetFrameWidth();
                    height = _virtualCameraManager.GetFrameHeight();
                    Console.WriteLine($"[UI] Используем размеры SHM клиента: {width}x{height}");
                }
                
                var imageData = _imageGenerator.GenerateTestImage(width, height);
                
                // Конвертируем в BitmapImage для WPF
                var bitmapImage = new BitmapImage();
                bitmapImage.BeginInit();
                bitmapImage.StreamSource = new MemoryStream(imageData);
                bitmapImage.EndInit();
                bitmapImage.Freeze(); // Для многопоточности

                // Отображаем изображение
                DisplayImage.Source = bitmapImage;

                // Отправляем кадр в виртуальную камеру, если она запущена
                if (_virtualCameraManager.IsStarted)
                {
                    Console.WriteLine($"[UI] Отправляем кадр {_frameCount + 1} размером {imageData.Length} байт в разрешении {width}x{height}");
                    // Отправляем асинхронно без ожидания, чтобы не блокировать UI
                    _ = Task.Run(async () => await _virtualCameraManager.SendFrameAsync(imageData));
                }
                else
                {
                    if (_frameCount % 10 == 0) // Выводим каждый 10-й кадр, чтобы не спамить
                    {
                        Console.WriteLine($"[UI] Кадр {_frameCount + 1} сгенерирован, но VCam не подключен");
                    }
                }

                // Обновляем счетчики
                _frameCount++;
                FrameCountText.Text = $"Кадров: {_frameCount}";

                if (!_isRunning)
                {
                    if (_virtualCameraManager.IsStarted)
                    {
                        StatusText.Text = "Кадр отправлен в VCam";
                    }
                    else
                    {
                        StatusText.Text = "Сгенерирован один кадр";
                    }
                }
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Ошибка: {ex.Message}";
                StopGeneration();
            }
        }

        private async void InitVCamButton_Click(object sender, RoutedEventArgs e)
        {
            Console.WriteLine($"[UI] {DateTime.Now:HH:mm:ss.fff} InitVCamButton_Click: Начало инициализации TCP клиента");
            
            if (_virtualCameraManager.IsInitialized)
            {
                StatusText.Text = "TCP клиент уже инициализирован";
                Console.WriteLine($"[UI] TCP клиент уже инициализирован");
                return;
            }

            InitVCamButton.IsEnabled = false;
            Console.WriteLine($"[UI] Вызываем InitializeAsync...");
            var success = await _virtualCameraManager.InitializeAsync("Android Cam");
            if (success)
            {
                StartVCamButton.IsEnabled = true;
                StatusText.Text = "TCP клиент готов к подключению";
                Console.WriteLine($"[UI] Инициализация успешна, активируем кнопку подключения");
            }
            else
            {
                InitVCamButton.IsEnabled = true;
                Console.WriteLine($"[UI] Ошибка инициализации, возвращаем кнопку");
            }
        }

        private async void StartVCamButton_Click(object sender, RoutedEventArgs e)
        {
            Console.WriteLine($"[UI] {DateTime.Now:HH:mm:ss.fff} StartVCamButton_Click: Начало подключения к VCam");
            
            if (!_virtualCameraManager.IsInitialized)
            {
                StatusText.Text = "Сначала инициализируйте TCP клиент";
                Console.WriteLine($"[UI] Ошибка: TCP клиент не инициализирован");
                return;
            }

            StartVCamButton.IsEnabled = false;

            if (_virtualCameraManager.IsStarted)
            {
                Console.WriteLine($"[UI] Отключаемся от VCam...");
                await _virtualCameraManager.StopAsync();
                StartVCamButton.Content = "📡 Подключиться к VCam";
            }
            else
            {
                Console.WriteLine($"[UI] Подключаемся к VCam...");
                var success = await _virtualCameraManager.StartAsync();
                if (success)
                {
                    StartVCamButton.Content = "⏹ Отключиться от VCam";
                    Console.WriteLine($"[UI] Подключение успешно, теперь можно отправлять кадры");
                }
                else
                {
                    Console.WriteLine($"[UI] Ошибка подключения к VCam");
                }
            }

            StartVCamButton.IsEnabled = true;
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

        private async void DebugVCamButton_Click(object sender, RoutedEventArgs e)
        {
            if (_debugClient == null)
            {
                _debugClient = new VirtualCameraDebugClient();
                _debugClient.StatusChanged += OnDebugStatusChanged;
                _debugClient.ErrorOccurred += OnDebugError;
                
                StatusText.Text = "Запуск отладки виртуальной камеры...";
                
                var success = await _debugClient.ConnectAsync();
                if (success)
                {
                    // Тестируем отправку изображения
                    var testImageData = _imageGenerator.GenerateTestImage(320, 240);
                    await _debugClient.SendJpegImageAsync(testImageData);
                    
                    DebugVCamButton.Content = "⏹ Стоп отладки";
                }
                else
                {
                    _debugClient.Dispose();
                    _debugClient = null;
                }
            }
            else
            {
                await _debugClient.DisconnectAsync();
                _debugClient.Dispose();
                _debugClient = null;
                DebugVCamButton.Content = "🔍 Отладка VCam";
                StatusText.Text = "Отладка остановлена";
            }
        }
        
        private void OnDebugStatusChanged(object? sender, string status)
        {
            Dispatcher.Invoke(() =>
            {
                VCamStatusText.Text = $"Отладка: {status}";
            });
        }

        private void OnDebugError(object? sender, string error)
        {
            Dispatcher.Invoke(() =>
            {
                VCamStatusText.Text = $"Отладка: ОШИБКА - {error}";
            });
        }

        protected override void OnClosed(EventArgs e)
        {
            StopGeneration();
            _virtualCameraManager?.Dispose();
            _debugClient?.Dispose();
            base.OnClosed(e);
        }
    }
} 