using System;
using System.Threading.Tasks;

namespace vCam
{
    public class VirtualCameraManager : IDisposable
    {
        private readonly VirtualCameraSharedMemClient _shmClient;
        private bool _isInitialized;
        private bool _isStarted;

        public event EventHandler<string>? StatusChanged;
        public event EventHandler<string>? ErrorOccurred;

        public bool IsInitialized => _isInitialized;
        public bool IsStarted => _isStarted;

        /// <summary>
        /// Получает ширину кадра от TCP клиента
        /// </summary>
        public int GetFrameWidth()
        {
            return _isStarted ? _shmClient.FrameWidth : 1920;
        }

        /// <summary>
        /// Получает высоту кадра от TCP клиента
        /// </summary>
        public int GetFrameHeight()
        {
            return _isStarted ? _shmClient.FrameHeight : 1080;
        }

        public VirtualCameraManager()
        {
            _shmClient = new VirtualCameraSharedMemClient();
            _shmClient.StatusChanged += OnShmStatusChanged;
            _shmClient.ErrorOccurred += OnShmError;
            _isInitialized = false;
            _isStarted = false;
        }

        /// <summary>
        /// Инициализирует подключение к DirectShow виртуальной камере
        /// </summary>
        /// <param name="cameraName">Имя виртуальной камеры (не используется для TCP)</param>
        /// <returns>true если инициализация прошла успешно</returns>
        public async Task<bool> InitializeAsync(string cameraName = "Android Cam")
        {
            try
            {
                if (_isInitialized)
                {
                    OnStatusChanged("TCP клиент уже инициализирован");
                    return true;
                }

                _isInitialized = true;
                OnStatusChanged("Shared memory клиент инициализирован");
                return true;
            }
            catch (Exception ex)
            {
                OnErrorOccurred($"Ошибка инициализации: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Синхронная версия Initialize для обратной совместимости
        /// </summary>
        public bool Initialize(string cameraName = "Android Cam")
        {
            return InitializeAsync(cameraName).Result;
        }

        /// <summary>
        /// Запускает подключение к виртуальной камере
        /// </summary>
        /// <returns>true если запуск прошел успешно</returns>
        public async Task<bool> StartAsync()
        {
            if (!_isInitialized)
            {
                OnErrorOccurred("Клиент не инициализирован");
                return false;
            }

            if (_isStarted)
            {
                OnStatusChanged("Уже подключен к виртуальной камере");
                return true;
            }

            try
            {
                var connected = await _shmClient.ConnectAsync();
                if (connected)
                {
                    _isStarted = true;
                    OnStatusChanged("Подключено к DirectShow виртуальной камере");
                    return true;
                }
                else
                {
                    OnErrorOccurred("Не удалось подключиться к виртуальной камере");
                    return false;
                }
            }
            catch (Exception ex)
            {
                OnErrorOccurred($"Ошибка запуска: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Синхронная версия Start для обратной совместимости
        /// </summary>
        public bool Start()
        {
            return StartAsync().Result;
        }

        /// <summary>
        /// Останавливает подключение к виртуальной камере
        /// </summary>
        public async Task StopAsync()
        {
            if (!_isStarted) return;

            try
            {
                await _shmClient.DisconnectAsync();
                _isStarted = false;
                OnStatusChanged("Отключено от DirectShow виртуальной камеры");
            }
            catch (Exception ex)
            {
                OnErrorOccurred($"Ошибка остановки: {ex.Message}");
            }
        }

        /// <summary>
        /// Синхронная версия Stop для обратной совместимости
        /// </summary>
        public void Stop()
        {
            StopAsync().Wait(2000);
        }

        /// <summary>
        /// Отправляет кадр в виртуальную камеру
        /// </summary>
        /// <param name="imageData">JPEG данные изображения</param>
        public async Task SendFrameAsync(byte[] imageData)
        {
            if (!_isStarted)
            {
                OnErrorOccurred("Не подключен к виртуальной камере");
                return;
            }

            try
            {
                var success = await _shmClient.SendFrameAsync(imageData);
                if (!success)
                {
                    OnErrorOccurred("Не удалось отправить кадр");
                }
            }
            catch (Exception ex)
            {
                OnErrorOccurred($"Ошибка отправки кадра: {ex.Message}");
            }
        }

        /// <summary>
        /// Синхронная версия SendFrame для обратной совместимости
        /// </summary>
        public void SendFrame(byte[] imageData)
        {
            SendFrameAsync(imageData).Wait(1000);
        }

        protected virtual void OnStatusChanged(string status)
        {
            StatusChanged?.Invoke(this, status);
        }

        protected virtual void OnErrorOccurred(string error)
        {
            ErrorOccurred?.Invoke(this, error);
        }

        private void OnShmStatusChanged(object? sender, string status)
        {
            OnStatusChanged($"SHM: {status}");
        }

        private void OnShmError(object? sender, string error)
        {
            OnErrorOccurred($"SHM: {error}");
        }

        public void Dispose()
        {
            Stop();
            _shmClient?.Dispose();
            _isInitialized = false;
        }
    }
} 