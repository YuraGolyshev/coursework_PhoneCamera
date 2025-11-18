package com.example.phonecamera

import android.Manifest
import android.app.Activity
import android.content.Context
import android.graphics.Bitmap
import android.graphics.BitmapFactory
import android.graphics.ImageFormat
import android.graphics.Rect
import android.graphics.YuvImage
import android.hardware.camera2.CameraCharacteristics
import android.hardware.camera2.CameraManager
import android.hardware.camera2.CaptureRequest
import android.net.Uri
import android.net.wifi.WifiManager
import android.os.Build
import android.os.Bundle
import android.os.Handler
import android.os.Looper
import android.os.PowerManager
import android.text.format.Formatter
import android.util.Log
import android.util.Range
import android.util.Size
import android.view.WindowManager
import android.widget.Toast
import androidx.activity.ComponentActivity
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.compose.setContent
import androidx.activity.enableEdgeToEdge
import androidx.activity.result.contract.ActivityResultContracts
import androidx.annotation.RequiresApi
import androidx.camera.camera2.interop.Camera2Interop
import androidx.camera.camera2.interop.ExperimentalCamera2Interop
import androidx.camera.core.CameraSelector
import androidx.camera.core.ImageAnalysis
import androidx.camera.core.ImageCapture
import androidx.camera.core.ImageCaptureException
import androidx.camera.core.ImageProxy
import androidx.camera.core.Preview
import androidx.camera.core.resolutionselector.ResolutionSelector
import androidx.camera.core.resolutionselector.ResolutionStrategy
import androidx.camera.lifecycle.ProcessCameraProvider
import androidx.camera.view.PreviewView
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.material3.Button
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.platform.LocalLifecycleOwner
import androidx.compose.ui.unit.dp
import androidx.compose.ui.viewinterop.AndroidView
import androidx.core.content.ContextCompat
import androidx.lifecycle.LifecycleOwner
import com.example.phonecamera.ui.theme.PhoneCameraTheme
import com.google.accompanist.permissions.ExperimentalPermissionsApi
import com.google.accompanist.permissions.isGranted
import com.google.accompanist.permissions.rememberPermissionState
import com.google.accompanist.permissions.shouldShowRationale
import com.google.zxing.integration.android.IntentIntegrator
import com.google.zxing.integration.android.IntentResult
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import java.io.ByteArrayOutputStream
import java.io.IOException
import java.io.OutputStream
import java.net.Socket
import java.nio.ByteBuffer
import java.nio.ByteOrder
import java.util.concurrent.ArrayBlockingQueue
import java.util.concurrent.Executors
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicBoolean
import java.util.concurrent.atomic.AtomicInteger

// Тег для логирования
private const val TAG = "CameraStream"
private const val TAG_CAM = "CameraModes"

// Очень простой протокол с уникальными маркерами
private val FRAME_START_MARKER = byteArrayOf(0x01, 0x02, 0x03, 0x04) // Явно отличимый маркер начала кадра 
private val FRAME_END_MARKER = byteArrayOf(0x04, 0x03, 0x02, 0x01) // Явно отличимый маркер конца кадра

class MainActivity : ComponentActivity() {
    private lateinit var wakeLock: PowerManager.WakeLock

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)

        // Не даём устройству уйти в сон
        window.addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)
        val pm = getSystemService(Context.POWER_SERVICE) as PowerManager
        wakeLock = pm.newWakeLock(PowerManager.PARTIAL_WAKE_LOCK, "CameraStream::Wakelock")
        wakeLock.acquire()

        enableEdgeToEdge()
        setContent {
            PhoneCameraTheme {
                Surface(
                    modifier = Modifier.fillMaxSize(),
                    color = MaterialTheme.colorScheme.background
                ) {
                    CameraApp()
                }
            }
        }
    }

    override fun onDestroy() {
        if (wakeLock.isHeld) wakeLock.release()
        super.onDestroy()
    }
}

@RequiresApi(Build.VERSION_CODES.P)
@androidx.annotation.OptIn(ExperimentalCamera2Interop::class)
@OptIn(ExperimentalPermissionsApi::class)
@Composable
fun CameraApp() {
    val context = LocalContext.current
    val lifecycleOwner = LocalLifecycleOwner.current

    // Состояния для интерфейса пользователя
    var ipAddress by remember { mutableStateOf("") }
    var port by remember { mutableStateOf("8888") }
    var isStreaming by remember { mutableStateOf(false) }
    var connectionStatus by remember { mutableStateOf("Не подключено") }
    var localIpAddress by remember { mutableStateOf(getLocalIpAddress(context)) }
    val lensFacing = remember { mutableStateOf(CameraSelector.LENS_FACING_BACK) }
    val rotateDeg = remember { java.util.concurrent.atomic.AtomicInteger(0) }
    
    // Состояние для отслеживания количества отправленных кадров
    val frameCounter = remember { AtomicInteger(0) }
    
    // Use ImageCapture only for fallback; main streaming via ImageAnalysis
    val imageCapture = remember { ImageCapture.Builder().build() }

    // Новый быстрый use-case для стрима
//    val imageAnalysis = remember {
//        //val builder = ImageAnalysis.Builder()
//            //.setTargetResolution(Size(1920, 1080))
//        val resolutionSelector = ResolutionSelector.Builder()
//            .setResolutionStrategy(
//                ResolutionStrategy(
//                    Size(1920, 1080),
//                    ResolutionStrategy.FALLBACK_RULE_CLOSEST_HIGHER
//                )
//            )
//            .build()
//        val builder = ImageAnalysis.Builder()
//            .setResolutionSelector(resolutionSelector)
//
//        val streamSpec = StreamSpecification.builder()
//            .setExpectedFrameRateRange(Range(30, 30))
//            .build()
//        // --- Camera2Interop: просим 30 FPS ---
//        try {
//            Camera2Interop.Extender(builder)
//                .setCaptureRequestOption(
//                    CaptureRequest.CONTROL_AE_TARGET_FPS_RANGE,
//                    Range(30, 30)
//                )
//        } catch (e: Exception) {
//            Log.w(TAG, "Не удалось установить диапазон FPS: ${e.message}")
//        }
//
//        builder
//            .setBackpressureStrategy(ImageAnalysis.STRATEGY_KEEP_ONLY_LATEST)
//            .setOutputImageFormat(ImageAnalysis.OUTPUT_IMAGE_FORMAT_YUV_420_888)
//            .build()
//    }
    val imageAnalysis = remember {
        // ResolutionSelector
        val resolutionSelector = ResolutionSelector.Builder()
            .setResolutionStrategy(
                ResolutionStrategy(
                    Size(1920, 1080),
                    ResolutionStrategy.FALLBACK_RULE_CLOSEST_HIGHER
                )
            )
            .build()

        val builder = ImageAnalysis.Builder()
            .setResolutionSelector(resolutionSelector)

        // АГРЕССИВНЫЕ НАСТРОЙКИ для максимального FPS
        try {
            val extender = Camera2Interop.Extender(builder)
            
            // ОПТИМИЗАЦИЯ 9: Фиксированный FPS 30
            extender.setCaptureRequestOption(
                CaptureRequest.CONTROL_AE_TARGET_FPS_RANGE,
                Range(30, 30)
            )
            
            // ОПТИМИЗАЦИЯ 10: Отключаем автофокус для стабильности FPS
//            extender.setCaptureRequestOption(
//                CaptureRequest.CONTROL_AF_MODE,
//                CaptureRequest.CONTROL_AF_MODE_OFF
//            )
            
            // ОПТИМИЗАЦИЯ 11: Фиксированная экспозиция
            extender.setCaptureRequestOption(
                CaptureRequest.CONTROL_AE_MODE,
                CaptureRequest.CONTROL_AE_MODE_ON
            )
            
            // ОПТИМИЗАЦИЯ 12: Отключаем стабилизацию для производительности
            extender.setCaptureRequestOption(
                CaptureRequest.LENS_OPTICAL_STABILIZATION_MODE,
                CaptureRequest.LENS_OPTICAL_STABILIZATION_MODE_OFF
            )
            
            Log.i(TAG, "Применены агрессивные настройки для максимального FPS")
        } catch (e: Exception) {
            Log.w(TAG, "Не удалось установить агрессивные настройки: ${e.message}")
            // Fallback: базовые настройки FPS
            try {
                Camera2Interop.Extender(builder)
                    .setCaptureRequestOption(
                        CaptureRequest.CONTROL_AE_TARGET_FPS_RANGE,
                        Range(25, 30)
                    )
                Log.i(TAG, "Используем FPS 25-30 как fallback")
            } catch (e2: Exception) {
                Log.e(TAG, "Fallback FPS тоже не сработал: ${e2.message}")
            }
        }

        builder
            .setBackpressureStrategy(ImageAnalysis.STRATEGY_KEEP_ONLY_LATEST)
            .setOutputImageFormat(ImageAnalysis.OUTPUT_IMAGE_FORMAT_YUV_420_888)
            .build()
    }


// Логируем поддерживаемые диапазоны FPS и разрешения единожды
    LaunchedEffect(Unit) {
        val camManager = context.getSystemService(Context.CAMERA_SERVICE) as CameraManager
        camManager.cameraIdList.forEach { id ->
            val ch = camManager.getCameraCharacteristics(id)
            val lens = ch.get(CameraCharacteristics.LENS_FACING)
            val ranges = ch.get(CameraCharacteristics.CONTROL_AE_AVAILABLE_TARGET_FPS_RANGES)

            // Получаем поддерживаемые разрешения для ImageAnalysis
            val streamConfigMap = ch.get(CameraCharacteristics.SCALER_STREAM_CONFIGURATION_MAP)
            val yuvResolutions = streamConfigMap?.getOutputSizes(ImageFormat.YUV_420_888)
                ?.sortedWith(compareBy({ it.width }, { it.height }))

            Log.i(TAG_CAM, "Камера $id (facing=$lens) FPS ranges: ${ranges?.joinToString()}")
            Log.i(TAG_CAM, "  Поддерживаемые разрешения YUV_420_888: ${yuvResolutions?.joinToString()}")

            // Если это логическая multi-cam, выводим физические ID
            val physicalIds = ch.physicalCameraIds
            if (!physicalIds.isNullOrEmpty()) {
                physicalIds.forEach { pid ->
                    val pChar = camManager.getCameraCharacteristics(pid)
                    val pRanges = pChar.get(CameraCharacteristics.CONTROL_AE_AVAILABLE_TARGET_FPS_RANGES)

                    // Разрешения для физической камеры
                    val pStreamConfigMap = pChar.get(CameraCharacteristics.SCALER_STREAM_CONFIGURATION_MAP)
                    val pYuvResolutions = pStreamConfigMap?.getOutputSizes(ImageFormat.YUV_420_888)
                        ?.sortedWith(compareBy({ it.width }, { it.height }))

                    Log.i(TAG_CAM, "  └─ Физическая $pid FPS: ${pRanges?.joinToString()}")
                    Log.i(TAG_CAM, "      Поддерживаемые разрешения YUV_420_888: ${pYuvResolutions?.joinToString()}")
                }
            }
        }
    }
    
    // Состояние для отслеживания и управления потоком захвата
    val isActive = remember { AtomicBoolean(false) }
    val coroutineScope = rememberCoroutineScope()
    
    // Запрос разрешения на использование камеры
    val cameraPermissionState = rememberPermissionState(Manifest.permission.CAMERA)
    
    // QR Scan launcher
    val qrLauncher = rememberLauncherForActivityResult(
        contract = ActivityResultContracts.StartActivityForResult()
    ) { res ->
        val result: IntentResult? = IntentIntegrator.parseActivityResult(res.resultCode, res.data)
        if (result != null && result.contents != null) {
            val uri = Uri.parse(result.contents)
            if (uri.scheme == "cam") {
                ipAddress = uri.host ?: ""
                if (uri.port != -1) port = uri.port.toString()
                Toast.makeText(context, "Считано $ipAddress", Toast.LENGTH_SHORT).show()
            }
        }
    }
    
    LaunchedEffect(Unit) {
        cameraPermissionState.launchPermissionRequest()
    }
    
    Column(
        modifier = Modifier.fillMaxSize(),
        horizontalAlignment = Alignment.CenterHorizontally
    ) {
        if (cameraPermissionState.status.isGranted) {
            // Предпросмотр + элементы управления поверх него
            Box(
                modifier = Modifier
                    .weight(1f)
                    .fillMaxWidth()
            ) {
                CameraPreview(
                    modifier = Modifier.fillMaxSize(),
                    lifecycleOwner = lifecycleOwner,
                    imageCapture = imageCapture,
                    imageAnalysis = imageAnalysis,
                    lensFacing = lensFacing.value
                )

                Column(
                    modifier = Modifier
                        .align(Alignment.BottomCenter)
                        .fillMaxWidth()
                        .background(MaterialTheme.colorScheme.surface)
                        .padding(16.dp)
                ) {
                    OutlinedTextField(
                        value = ipAddress,
                        onValueChange = { ipAddress = it },
                        label = { Text("IP") },
                        modifier = Modifier.fillMaxWidth()
                    )
                    Spacer(modifier = Modifier.height(8.dp))
                    OutlinedTextField(
                        value = port,
                        onValueChange = { port = it },
                        label = { Text("Порт") },
                        modifier = Modifier.fillMaxWidth()
                    )
                    Spacer(modifier = Modifier.height(8.dp))

                    Button(
                        onClick = {
                            if (isStreaming) {
                                isActive.set(false)
                                isStreaming = false
                                connectionStatus = "Передача остановлена"
                            } else {
                                if (ipAddress.isNotBlank()) {
                                    isStreaming = true
                                    frameCounter.set(0)
                                    connectionStatus = "Подключение..."
                                    coroutineScope.launch {
                                        startStreamingFast(
                                            context,
                                            ipAddress,
                                            port.toIntOrNull() ?: 8888,
                                            imageAnalysis,
                                            isActive,
                                            frameCounter,
                                            rotateDeg,
                                            { connectionStatus = it },
                                            { err ->
                                                isStreaming = false
                                                isActive.set(false)
                                                connectionStatus = "Ошибка: $err"
                                            }
                                        )
                                    }
                                } else Toast.makeText(context, "Введите IP", Toast.LENGTH_SHORT).show()
                            }
                        },
                        modifier = Modifier.fillMaxWidth()
                    ) { Text(if (isStreaming) "Стоп" else "Старт") }

                    Spacer(modifier = Modifier.height(8.dp))

                    Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.SpaceBetween) {
//                        Button(onClick = {
//                            lensFacing.value = if (lensFacing.value == CameraSelector.LENS_FACING_BACK)
//                                CameraSelector.LENS_FACING_FRONT else CameraSelector.LENS_FACING_BACK
//                        }) { Text(if (lensFacing.value == CameraSelector.LENS_FACING_BACK) "Фронт" else "Тыл") }
//
//                        Button(onClick = { rotateDeg.set((rotateDeg.get() + 90) % 360) }) { Text("Повернуть") }

                        Button(onClick = {
                            val integrator = com.google.zxing.integration.android.IntentIntegrator(context as Activity)
                            integrator.setCaptureActivity(CapturePortrait::class.java)
                            integrator.setPrompt("Сканируйте QR с ПК")
                            integrator.setBeepEnabled(false)
                            integrator.setOrientationLocked(true)
                            qrLauncher.launch(integrator.createScanIntent())
                        }) { Text("QR") }
                    }
                }
            }
            
            Spacer(modifier = Modifier.height(8.dp))
            
            Text(
                text = "Статус: $connectionStatus",
                style = MaterialTheme.typography.bodyMedium
            )

            if (isStreaming) {
                Text(
                    text = "Отправлено кадров: ${frameCounter.get()}",
                    style = MaterialTheme.typography.bodyMedium
                )
            }
        } else {
            // Отображение сообщения, если разрешения не предоставлены
            Column(
                modifier = Modifier
                    .fillMaxSize()
                    .padding(16.dp),
                verticalArrangement = Arrangement.Center,
                horizontalAlignment = Alignment.CenterHorizontally
            ) {
                val textToShow = if (cameraPermissionState.status.shouldShowRationale) {
                    "Доступ к камере необходим для работы приложения. Пожалуйста, предоставьте разрешение в настройках."
                } else {
                    "Для работы приложения необходим доступ к камере."
                }
                
                Text(textToShow)
                
                Spacer(modifier = Modifier.height(16.dp))
                
                Button(onClick = { cameraPermissionState.launchPermissionRequest() }) {
                    Text("Запросить разрешение")
                }
            }
        }
    }
}

@Composable
fun CameraPreview(
    modifier: Modifier,
    lifecycleOwner: LifecycleOwner,
    imageCapture: ImageCapture,
    imageAnalysis: ImageAnalysis,
    lensFacing: Int
) {
    val context = LocalContext.current
    val previewView = remember { PreviewView(context) }
    
    AndroidView(
        factory = { previewView },
        modifier = modifier
    ) {
        val cameraProviderFuture = ProcessCameraProvider.getInstance(context)
        cameraProviderFuture.addListener({
            val cameraProvider = cameraProviderFuture.get()
            val preview = Preview.Builder().build().also {
                it.setSurfaceProvider(previewView.surfaceProvider)
            }
            
            try {
                cameraProvider.unbindAll()
                val selector = CameraSelector.Builder().requireLensFacing(lensFacing).build()
                cameraProvider.bindToLifecycle(
                    lifecycleOwner,
                    selector,
                    preview,
                    imageCapture,
                    imageAnalysis
                )
            } catch (e: Exception) {
                Log.e(TAG, "Ошибка при привязке камеры: ${e.message}")
            }
        }, ContextCompat.getMainExecutor(context))
    }
}

suspend fun startStreaming(
    context: Context,
    ipAddress: String,
    port: Int,
    imageCapture: ImageCapture,
    isActive: AtomicBoolean,
    frameCounter: AtomicInteger,
    onStatusChanged: (String) -> Unit,
    onError: (String) -> Unit
) = withContext(Dispatchers.IO) {
    try {
        Log.i(TAG, "Начинаем подключение к $ipAddress:$port")
        isActive.set(true)
        
        // Создаем сокет для подключения
        val socket = Socket(ipAddress, port).apply {
            soTimeout = 5000  // 5 секунд таймаут чтения
            keepAlive = true  // Поддерживать подключение активным
            sendBufferSize = 1_048_576
            tcpNoDelay = true
        }
        val outputStream = socket.getOutputStream()
        
        Log.i(TAG, "Соединение успешно установлено с $ipAddress:$port")
        onStatusChanged("Подключено к $ipAddress:$port")
        
        // Создаем Handler для выполнения кода в основном потоке
        val mainHandler = Handler(Looper.getMainLooper())
        
        // Поток для захвата и отправки изображений
        val cameraExecutor = Executors.newSingleThreadScheduledExecutor()
        
        // Тестовый пакет для проверки соединения
        Log.i(TAG, "Отправляем тестовый пакет...")
        try {
            val testData = "CAMERA_TEST_PACKET".toByteArray()
            
            // Отправляем маркеры и тестовые данные используя новый протокол
            outputStream.write(FRAME_START_MARKER)  // Маркер начала
            
            // Размер данных (4 байта)
            val sizeBuffer = ByteBuffer.allocate(4).order(ByteOrder.LITTLE_ENDIAN).putInt(testData.size).array()
            outputStream.write(sizeBuffer)
            
            // Данные
            outputStream.write(testData)
            
            // Маркер конца
            outputStream.write(FRAME_END_MARKER)
            outputStream.flush()
        } catch (e: Exception) {
            Log.e(TAG, "Ошибка при отправке тестового пакета: ${e.message}")
            onError("Ошибка отправки тестовых данных: ${e.message}")
            socket.close()
            return@withContext
        }

        // Небольшая пауза после отправки тестового пакета
        delay(500)
        
        // Сначала отправим одно изображение напрямую, чтобы проверить захват
        try {
            Log.i(TAG, "Пробуем отправить тестовый кадр напрямую...")
            captureAndSendImage(context, imageCapture, outputStream, frameCounter, onStatusChanged)
            // Дадим время на захват изображения
            delay(1000)
        } catch (e: Exception) {
            Log.e(TAG, "Ошибка при отправке тестового кадра: ${e.message}", e)
        }
        
        // Теперь запускаем регулярные отправки
        Log.i(TAG, "Запускаем регулярную отправку изображений")
        cameraExecutor.scheduleAtFixedRate({
            if (!isActive.get()) {
                Log.i(TAG, "Остановка потока отправки")
                cameraExecutor.shutdown()
                try {
                    socket.close()
                } catch (e: Exception) {
                    Log.e(TAG, "Ошибка при закрытии сокета: ${e.message}")
                }
                return@scheduleAtFixedRate
            }
            
            try {
                captureAndSendImage(context, imageCapture, outputStream, frameCounter, onStatusChanged)
            } catch (e: Exception) {
                Log.e(TAG, "Ошибка при отправке изображения: ${e.message}", e)
                // Используем Handler для выполнения кода в основном потоке
                mainHandler.post {
                    onError(e.message ?: "Неизвестная ошибка")
                }
                isActive.set(false)
                cameraExecutor.shutdown()
                try {
                    socket.close()
                } catch (e: Exception) {
                    // Игнорируем ошибки при закрытии
                }
            }
        }, 0L, 16L, TimeUnit.MILLISECONDS)  // ~60 FPS (1000мс/60 = ~16.7мс)
        
        // Используем Thread.sleep вместо suspend-функции delay
        while (isActive.get()) {
            try {
                Thread.sleep(100L)
            } catch (e: InterruptedException) {
                break
            }
        }
        
        // Когда поток остановлен, закрываем ресурсы
        Log.i(TAG, "Завершение потока и закрытие ресурсов")
        cameraExecutor.shutdownNow()
        socket.close()
        
    } catch (e: Exception) {
        Log.e(TAG, "Ошибка подключения: ${e.message}", e)
        withContext(Dispatchers.Main) {
            onError("Ошибка подключения: ${e.message}")
        }
    }
}

private fun captureAndSendImage(
    context: Context, 
    imageCapture: ImageCapture, 
    outputStream: OutputStream,
    frameCounter: AtomicInteger,
    onStatusChanged: (String) -> Unit
) {
    Log.i(TAG, "Запуск захвата изображения с камеры...")

    try {
        imageCapture.takePicture(
            ContextCompat.getMainExecutor(context),
            object : ImageCapture.OnImageCapturedCallback() {
                override fun onCaptureSuccess(image: ImageProxy) {
                    try {
                        Log.i(TAG, "Захват изображения успешен, размер: ${image.width}x${image.height}, формат: ${image.format}")
                        
                        // Отладочная информация о состоянии буфера
                        val buffer = image.planes[0].buffer
                        Log.d(TAG, "Состояние буфера: размер=${buffer.capacity()}, позиция=${buffer.position()}, лимит=${buffer.limit()}, осталось=${buffer.remaining()}")
                        
                        val data = ByteArray(buffer.remaining())
                        buffer.get(data)
                        Log.d(TAG, "Получено сырых данных: ${data.size} байт")
                        
                        // Больше информации о сырых данных
                        if (data.size > 0) {
                            val head = data.take(20).joinToString(" ") { String.format("%02X", it) }
                            Log.d(TAG, "Начало сырых данных: $head")
                        }
                        
                        // Попробуем определить тип данных, которые мы получили
                        val isJpeg = data.size >= 2 && data[0].toInt() == 0xFF && data[1].toInt() == 0xD8
                        val isHeic = data.size >= 4 && data[0].toInt() == 0x00 && data[1].toInt() == 0x00 && 
                                    data[2].toInt() == 0x00 && data[3].toInt() == 0x18
                        
                        Log.d(TAG, "Формат данных: ${if(isJpeg) "JPEG" else if(isHeic) "HEIC" else "неизвестен"}")
                        
                        // Преобразование необработанных данных в Bitmap с возможным понижением разрешения
                        Log.d(TAG, "Пытаюсь декодировать данные в Bitmap...")
                        val options = BitmapFactory.Options().apply {
                            inJustDecodeBounds = true
                        }
                        BitmapFactory.decodeByteArray(data, 0, data.size, options)
                        Log.d(TAG, "Параметры исходного изображения: ${options.outWidth}x${options.outHeight}, тип: ${options.outMimeType}")
                        
                        options.inJustDecodeBounds = false
                        val bitmap = BitmapFactory.decodeByteArray(data, 0, data.size, options)
                        
                        if (bitmap == null) {
                            Log.e(TAG, "Не удалось декодировать изображение в Bitmap")
                            
                            // Попробуем альтернативные способы получения изображения
                            Log.d(TAG, "Пробую альтернативный подход с ImageReader...")
                            try {
                                val yuvImage = YuvImage(
                                    data, ImageFormat.NV21, image.width, image.height, null
                                )
                                val out = ByteArrayOutputStream()
                                yuvImage.compressToJpeg(
                                    Rect(0, 0, image.width, image.height), 100, out
                                )
                                val jpegData = out.toByteArray()
                                
                                val jpegBitmap = BitmapFactory.decodeByteArray(jpegData, 0, jpegData.size)
                                if (jpegBitmap != null) {
                                    Log.d(TAG, "Успешно получил Bitmap через YuvImage")
                                    
                                    // Отправляем как обычно, с масштабированием до Full HD
                                    val scaledBitmap = Bitmap.createScaledBitmap(jpegBitmap, 1920, 1080, true)
                                    val byteArrayOutputStream = ByteArrayOutputStream()
                                    scaledBitmap.compress(Bitmap.CompressFormat.JPEG, 90, byteArrayOutputStream)
                                    
                                    // Запускаем отправку в фоновом потоке
                                    Thread {
                                        sendImageBytes(byteArrayOutputStream.toByteArray(), outputStream, frameCounter, onStatusChanged)
                                    }.start()
                                    
                                    // Освобождаем ресурсы
                                    jpegBitmap.recycle()
                                    scaledBitmap.recycle()
                                    return
                                } else {
                                    Log.e(TAG, "Не удалось декодировать JPEG из YuvImage")
                                }
                            } catch (e: Exception) {
                                Log.e(TAG, "Ошибка при использовании YuvImage: ${e.message}", e)
                            }
                            
                            return
                        }
                        
                        Log.d(TAG, "Bitmap создан, размер: ${bitmap.width}x${bitmap.height}, конфигурация: ${bitmap.config}")
                        
                        // Больше логирования для масштабирования
                        Log.d(TAG, "Масштабирование Bitmap до 1920x1080...")
                        val scaledBitmap = Bitmap.createScaledBitmap(bitmap, 1920, 1080, true)
                        Log.d(TAG, "Bitmap масштабирован до ${scaledBitmap.width}x${scaledBitmap.height}")
                        
                        Log.d(TAG, "Сжатие Bitmap в JPEG...")
                        val byteArrayOutputStream = ByteArrayOutputStream()
                        val success = scaledBitmap.compress(Bitmap.CompressFormat.JPEG, 85, byteArrayOutputStream)
                        
                        if (!success) {
                            Log.e(TAG, "Не удалось сжать Bitmap в JPEG")
                            return
                        }
                        
                        val imageBytes = byteArrayOutputStream.toByteArray()
                        val imageSize = imageBytes.size
                        
                        Log.i(TAG, "Изображение сжато до $imageSize байт, отправляю...")
                        
                        // Запускаем отправку в фоновом потоке
                        Thread {
                            try {
                                Log.d(TAG, "Начинаю отправку в фоновом потоке...")
                                sendImageBytes(imageBytes, outputStream, frameCounter, onStatusChanged)
                            } catch (e: Exception) {
                                Log.e(TAG, "Ошибка при отправке изображения в фоновом потоке: ${e.message}", e)
                            }
                        }.start()
                        
                        // Освобождаем ресурсы
                        bitmap.recycle()
                        scaledBitmap.recycle()
                    } catch (e: Exception) {
                        Log.e(TAG, "Ошибка при обработке изображения: ${e.message}", e)
                    } finally {
                        image.close()
                    }
                }
                
                override fun onError(exception: ImageCaptureException) {
                    Log.e(TAG, "Ошибка захвата изображения: код ${exception.imageCaptureError}, сообщение: ${exception.message}", exception)
                    
                    // Просто логируем код ошибки без использования специфических констант
                    Log.e(TAG, "Код ошибки захвата: ${exception.imageCaptureError}")
                }
            }
        )
    } catch (e: Exception) {
        Log.e(TAG, "Исключение при запуске захвата: ${e.message}", e)
    }
}

// Отдельный метод для отправки данных изображения
private fun sendImageBytes(
    imageBytes: ByteArray,
    outputStream: OutputStream,
    frameCounter: AtomicInteger,
    onStatusChanged: (String) -> Unit
) {
    try {
        val imageSize = imageBytes.size
        Log.i(TAG, "Начинаю отправку изображения размером $imageSize байт...")
        
        // 1. Маркер начала кадра
        outputStream.write(FRAME_START_MARKER)
        Log.d(TAG, "Маркер начала отправлен: ${FRAME_START_MARKER.joinToString(" ") { String.format("%02X", it) }}")
        
        // 2. Длина данных (4 байта)
        val sizeBuffer = ByteBuffer.allocate(4)
            .order(ByteOrder.LITTLE_ENDIAN)
            .putInt(imageSize)
            .array()
        outputStream.write(sizeBuffer)
        Log.d(TAG, "Размер отправлен: ${sizeBuffer.joinToString(" ") { String.format("%02X", it) }} ($imageSize байт)")
        
        // 3. Данные изображения (ОПТИМИЗАЦИЯ 14: большие блоки для производительности)
        val blockSize = 65536 // 64KB блоки для лучшей производительности
        var sentBytes = 0
        
        while (sentBytes < imageBytes.size) {
            val remainingBytes = imageBytes.size - sentBytes
            val bytesToSend = minOf(blockSize, remainingBytes)
            
            outputStream.write(imageBytes, sentBytes, bytesToSend)
            
            sentBytes += bytesToSend
            // Логируем реже для производительности
            if (sentBytes % (blockSize * 4) == 0) {
                Log.d(TAG, "Отправлено $sentBytes из $imageSize байт (${(sentBytes * 100) / imageSize}%)")
            }
        }
        
        // 4. Маркер конца кадра
        outputStream.write(FRAME_END_MARKER)
        outputStream.flush()
        Log.d(TAG, "Маркер конца отправлен: ${FRAME_END_MARKER.joinToString(" ") { String.format("%02X", it) }}")
        
        Log.i(TAG, "Кадр успешно отправлен")
        
        // Увеличиваем счетчик кадров и обновляем статус
        val framesCount = frameCounter.incrementAndGet()
        
        Log.d(TAG, "Счётчик кадров увеличен: $framesCount")
        
        if (framesCount % 10 == 0 || framesCount == 1) {
            Log.i(TAG, "Отправлено кадров: $framesCount")
            onStatusChanged("Отправлено кадров: $framesCount")
        }
    } catch (e: Exception) {
        Log.e(TAG, "Ошибка при отправке данных: ${e.message}", e)
    }
}

// Функция для получения локального IP-адреса устройства
fun getLocalIpAddress(context: Context): String {
    val wifiManager = context.applicationContext.getSystemService(Context.WIFI_SERVICE) as WifiManager
    val ipAddress = wifiManager.connectionInfo.ipAddress
    return Formatter.formatIpAddress(ipAddress)
}

// ------------------ БЫСТРЫЙ СТРИМ НА БАЗЕ ImageAnalysis ------------------

// Добавляем константы для логирования
private const val LOG_FPS_INTERVAL = 5000L // Интервал замера FPS (мс)
private const val DETAILED_LOGS = true // Включить детальные логи для отладки

suspend fun startStreamingFast(
    context: Context,
    ipAddress: String,
    port: Int,
    imageAnalysis: ImageAnalysis,
    isActive: AtomicBoolean,
    frameCounter: AtomicInteger,
    rotateDeg: java.util.concurrent.atomic.AtomicInteger,
    onStatusChanged: (String) -> Unit,
    onError: (String) -> Unit
) = withContext(Dispatchers.IO) {
    try {
        Log.i(TAG, "[fast] Подключаемся к $ipAddress:$port ...")

        val socket = Socket(ipAddress, port).apply {
            soTimeout = 5000
            keepAlive = true
            tcpNoDelay = true
            sendBufferSize = 2_097_152
            receiveBufferSize = 1_048_576
        }

        val stream = socket.getOutputStream()
        val socketSendLock = Object() // Для синхронизации отправки

        onStatusChanged("Подключено (fast)")
        isActive.set(true)

        val cameraExecutor = Executors.newSingleThreadExecutor()
        val frameQueue: ArrayBlockingQueue<ImageProxy> = ArrayBlockingQueue(3)
        val encodePool = Executors.newFixedThreadPool(2)

        // Статистика производительности
        var totalFramesProcessed = 0L
        var totalNetworkTime = 0L
        var totalProcessingTime = 0L
        var framesDropped = 0
        var lastFpsLogTime = System.currentTimeMillis()

        // Мониторинг размера очереди
        val maxQueueSize = AtomicInteger(0)
        val queueMonitor = Executors.newSingleThreadScheduledExecutor()
        queueMonitor.scheduleAtFixedRate({
            val size = frameQueue.size
            val max = maxQueueSize.get()
            if (size > max) maxQueueSize.set(size)
        }, 0, 100, TimeUnit.MILLISECONDS)

        repeat(2) {
            encodePool.execute {
                var reusableNV21: ByteArray? = null
                var reusableBaos = ByteArrayOutputStream(512 * 1024)

                while (true) {
                    val img = try {
                        frameQueue.take()
                    } catch (ie: InterruptedException) {
                        break
                    }

                    val frameStartTime = System.currentTimeMillis()
                    val frameId = frameCounter.incrementAndGet()

                    try {
                        // Замер времени преобразования
                        val conversionStart = System.currentTimeMillis()
                        val width = img.width
                        val height = img.height
                        val expectedSize = width * height * 3 / 2

                        if (reusableNV21 == null || reusableNV21!!.size != expectedSize) {
                            reusableNV21 = ByteArray(expectedSize)
                        }

                        copyImageToNV21Optimized(img, reusableNV21!!)
                        val conversionTime = System.currentTimeMillis() - conversionStart

                        // Замер времени кодирования
                        val encodingStart = System.currentTimeMillis()
                        reusableBaos.reset()

                        val yuvImage = YuvImage(reusableNV21, ImageFormat.NV21, width, height, null)
                        yuvImage.compressToJpeg(Rect(0, 0, width, height), 40, reusableBaos)
                        val jpegData = reusableBaos.toByteArray()
                        val encodingTime = System.currentTimeMillis() - encodingStart

                        // Замер времени ротации
                        var rotationTime = 0L
                        var finalJpeg = jpegData
                        val rot = rotateDeg.get()

                        if (rot != 0) {
                            val rotationStart = System.currentTimeMillis()
                            finalJpeg = rotateJpegOptimized(jpegData, rot) ?: jpegData
                            rotationTime = System.currentTimeMillis() - rotationStart
                        }

                        // Замер времени отправки
                        val sendStart = System.currentTimeMillis()
                        synchronized(socketSendLock) {
                            sendImageBytes(finalJpeg, stream, frameCounter, onStatusChanged)
                        }
                        val sendTime = System.currentTimeMillis() - sendStart

                        totalProcessingTime += conversionTime + encodingTime + rotationTime
                        totalNetworkTime += sendTime
                        totalFramesProcessed++

                        if (DETAILED_LOGS) {
                            Log.d(TAG, "[frame-$frameId] " +
                                    "conv:${conversionTime}ms, " +
                                    "enc:${encodingTime}ms, " +
                                    "rot:${rotationTime}ms, " +
                                    "send:${sendTime}ms, " +
                                    "size:${finalJpeg.size / 1024}KB")
                        }

                        // Логирование FPS каждые 5 секунд
                        val currentTime = System.currentTimeMillis()
                        if (currentTime - lastFpsLogTime > LOG_FPS_INTERVAL) {
                            val fps = (totalFramesProcessed * 1000 / (currentTime - lastFpsLogTime)).toDouble()
                            Log.i(TAG, "[perf] FPS: ${"%.1f".format(fps)}, " +
                                    "avg processing: ${totalProcessingTime/totalFramesProcessed}ms, " +
                                    "avg network: ${totalNetworkTime/totalFramesProcessed}ms, " +
                                    "queue: ${frameQueue.size}/${maxQueueSize.get()}, " +
                                    "dropped: $framesDropped")

                            // Сброс счетчиков
                            totalFramesProcessed = 0
                            totalProcessingTime = 0
                            totalNetworkTime = 0
                            maxQueueSize.set(0)
                            lastFpsLogTime = currentTime
                        }

                    } catch (e: Exception) {
                        Log.e(TAG, "[frame-$frameId] processing error: ${e.message}", e)
                    } finally {
                        img.close()
                    }
                }
            }
        }

        imageAnalysis.setAnalyzer(cameraExecutor) { img ->
            if (!isActive.get()) {
                img.close(); return@setAnalyzer
            }
            if (!frameQueue.offer(img)) {
                framesDropped++
                img.close()
                if (DETAILED_LOGS) {
                    Log.d(TAG, "[queue] frame dropped, total: $framesDropped")
                }
            }
        }

        while (isActive.get()) {
            delay(100)
        }

        // Остановка
        imageAnalysis.clearAnalyzer()
        cameraExecutor.shutdown()
        queueMonitor.shutdownNow()
        socket.close()
        encodePool.shutdownNow()
        Log.i(TAG, "[fast] Стрим остановлен. Total dropped: $framesDropped")

    } catch (e: Exception) {
        Log.e(TAG, "[fast] Ошибка стрима: ${e.message}", e)
        withContext(Dispatchers.Main) {
            onError("Ошибка: ${e.message}")
        }
    }
}

// Добавляем логирование в функцию отправки
fun AtomicInteger.sendImageBytes(
    data: ByteArray,
    stream: OutputStream,
    onStatusChanged: (String) -> Unit
) {
    try {
        // Отправка размера кадра
        val header = ByteBuffer.allocate(4).putInt(data.size).array()
        stream.write(header)
        stream.write(data)
        stream.flush()
    } catch (e: IOException) {
        Log.e(TAG, "[send] Ошибка отправки кадра ${get()}: ${e.message}")
        throw e
    }
}

//// ОПТИМИЗИРОВАННОЕ преобразование ImageProxy YUV_420_888 → NV21
//private fun imageToNV21(image: ImageProxy): ByteArray {
//    val width = image.width
//    val height = image.height
//
//    val yBuffer = image.planes[0].buffer
//    val uBuffer = image.planes[1].buffer
//    val vBuffer = image.planes[2].buffer
//
//    val yRowStride = image.planes[0].rowStride
//    val uvRowStride = image.planes[1].rowStride
//    val uvPixelStride = image.planes[1].pixelStride
//
//    val nv21 = ByteArray(width * height * 3 / 2)
//
//    // ОПТИМИЗАЦИЯ 1: Быстрое копирование Y-плоскости
//    if (yRowStride == width) {
//        // Если stride равен ширине, копируем все одним блоком
//        yBuffer.rewind()
//        yBuffer.get(nv21, 0, width * height)
//    } else {
//        // Копируем по строкам
//        var pos = 0
//        for (row in 0 until height) {
//            yBuffer.position(row * yRowStride)
//            yBuffer.get(nv21, pos, width)
//            pos += width
//        }
//    }
//
//    // ОПТИМИЗАЦИЯ 2: Быстрое копирование UV-плоскости
//    val chromaHeight = height / 2
//    val chromaWidth = width / 2
//    var uvPos = width * height
//
//    if (uvPixelStride == 2 && uvRowStride == width) {
//        // Оптимальный случай: UV данные уже в правильном формате
//        // Копируем блоками по строкам
//        for (row in 0 until chromaHeight) {
//            val uRowStart = row * uvRowStride
//            val vRowStart = row * uvRowStride
//
//            // Чередуем V и U (NV21 формат)
//            for (col in 0 until chromaWidth) {
//                val uIndex = uRowStart + col * 2
//                val vIndex = vRowStart + col * 2
//
//                nv21[uvPos++] = vBuffer.get(vIndex)
//                nv21[uvPos++] = uBuffer.get(uIndex)
//            }
//        }
//    } else {
//        // Медленный fallback для нестандартных форматов
//        for (row in 0 until chromaHeight) {
//            val uRowStart = row * uvRowStride
//            val vRowStart = row * uvRowStride
//            for (col in 0 until chromaWidth) {
//                val uIndex = uRowStart + col * uvPixelStride
//                val vIndex = vRowStart + col * uvPixelStride
//
//                nv21[uvPos++] = vBuffer.get(vIndex)
//                nv21[uvPos++] = uBuffer.get(uIndex)
//            }
//        }
//    }
//
//    return nv21
//}

// МАКСИМАЛЬНО ОПТИМИЗИРОВАННАЯ функция копирования в существующий буфер
private fun copyImageToNV21Optimized(image: ImageProxy, nv21: ByteArray) {
    val width = image.width
    val height = image.height

    val yBuffer = image.planes[0].buffer
    val uBuffer = image.planes[1].buffer
    val vBuffer = image.planes[2].buffer

    val yRowStride = image.planes[0].rowStride
    val uvRowStride = image.planes[1].rowStride
    val uvPixelStride = image.planes[1].pixelStride

    // СУПЕР-ОПТИМИЗАЦИЯ: Копирование Y-плоскости одним блоком если возможно
    if (yRowStride == width) {
        yBuffer.rewind()
        yBuffer.get(nv21, 0, width * height)
    } else {
        // Копирование по строкам для Y
        var pos = 0
        for (row in 0 until height) {
            yBuffer.position(row * yRowStride)
            yBuffer.get(nv21, pos, width)
            pos += width
        }
    }

    // СУПЕР-ОПТИМИЗАЦИЯ UV: прямое копирование если формат совпадает
    val chromaHeight = height / 2
    val chromaWidth = width / 2
    var uvPos = width * height

    // Оптимальный случай: UV в формате NV21 (V,U чередующиеся)
    if (uvPixelStride == 2 && uvRowStride == width) {
        for (row in 0 until chromaHeight) {
            vBuffer.position(row * uvRowStride)
            uBuffer.position(row * uvRowStride)

            for (col in 0 until chromaWidth) {
                nv21[uvPos++] = vBuffer.get(row * uvRowStride + col * 2)
                nv21[uvPos++] = uBuffer.get(row * uvRowStride + col * 2)
            }
        }
    } else {
        // Fallback для других форматов
        for (row in 0 until chromaHeight) {
            for (col in 0 until chromaWidth) {
                val uIndex = row * uvRowStride + col * uvPixelStride
                val vIndex = row * uvRowStride + col * uvPixelStride

                nv21[uvPos++] = vBuffer.get(vIndex)
                nv21[uvPos++] = uBuffer.get(uIndex)
            }
        }
    }
}

// ОПТИМИЗИРОВАННАЯ ротация JPEG без промежуточных bitmap'ов там где возможно
private fun rotateJpegOptimized(jpegData: ByteArray, degrees: Int): ByteArray? {
    return try {
        // Кэшируем Matrix для переиспользования
        val bmp = BitmapFactory.decodeByteArray(jpegData, 0, jpegData.size)
        
        if (bmp == null) return null
        
        // ОПТИМИЗАЦИЯ: используем минимальную конфигурацию для ускорения
        val matrix = android.graphics.Matrix().apply { 
            postRotate(degrees.toFloat()) 
        }
        
        // ОПТИМИЗАЦИЯ: используем фильтрацию false для скорости
        val rotated = Bitmap.createBitmap(bmp, 0, 0, bmp.width, bmp.height, matrix, false)
        
        val baos = ByteArrayOutputStream(jpegData.size) // предварительно задаем размер
        
        // ОПТИМИЗАЦИЯ: оптимальное качество для скорости
        val compressed = rotated.compress(Bitmap.CompressFormat.JPEG, 80, baos)
        
        bmp.recycle()
        rotated.recycle()
        
        if (compressed) baos.toByteArray() else null
    } catch (e: Exception) {
        Log.e(TAG, "Optimized rotate error: ${e.message}")
        null
    }
}

// ------------------ КОНЕЦ БЫСТРОГО СТРИМА ------------------