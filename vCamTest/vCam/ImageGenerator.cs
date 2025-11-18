using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;

namespace vCam
{
    public class ImageGenerator
    {
        private readonly Random _random;
        private int _frameCounter;

        public ImageGenerator()
        {
            _random = new Random();
            _frameCounter = 0;
        }

        /// <summary>
        /// Генерирует тестовое изображение в виде массива байтов JPEG
        /// </summary>
        /// <param name="width">Ширина изображения</param>
        /// <param name="height">Высота изображения</param>
        /// <returns>Массив байтов JPEG изображения</returns>
        public byte[] GenerateTestImage(int width = 640, int height = 480)
        {
            _frameCounter++;
            
            using (var bitmap = new Bitmap(width, height))
            using (var graphics = Graphics.FromImage(bitmap))
            {
                // Заливаем фон случайным цветом
                var backgroundColor = Color.FromArgb(
                    _random.Next(50, 150),
                    _random.Next(50, 150), 
                    _random.Next(50, 150)
                );
                graphics.Clear(backgroundColor);

                // Рисуем движущийся круг
                var circleX = (int)(width / 2 + Math.Sin(_frameCounter * 0.1) * width / 4);
                var circleY = (int)(height / 2 + Math.Cos(_frameCounter * 0.05) * height / 4);
                var circleSize = 50 + (int)(Math.Sin(_frameCounter * 0.2) * 20);
                
                using (var circleBrush = new SolidBrush(Color.Red))
                {
                    graphics.FillEllipse(circleBrush, circleX - circleSize/2, circleY - circleSize/2, circleSize, circleSize);
                }

                // Рисуем информацию о кадре
                var frameText = $"Кадр: {_frameCounter}";
                var timeText = $"Время: {DateTime.Now:HH:mm:ss.fff}";
                
                using (var font = new Font("Arial", 14, FontStyle.Bold))
                using (var textBrush = new SolidBrush(Color.White))
                {
                    graphics.DrawString(frameText, font, textBrush, 10, 10);
                    graphics.DrawString(timeText, font, textBrush, 10, 40);
                }

                // Рисуем несколько случайных прямоугольников
                for (int i = 0; i < 3; i++)
                {
                    var rectX = _random.Next(0, width - 100);
                    var rectY = _random.Next(0, height - 100);
                    var rectWidth = _random.Next(50, 100);
                    var rectHeight = _random.Next(50, 100);
                    
                    var rectColor = Color.FromArgb(
                        128, // полупрозрачность
                        _random.Next(100, 255),
                        _random.Next(100, 255),
                        _random.Next(100, 255)
                    );
                    
                    using (var rectBrush = new SolidBrush(rectColor))
                    {
                        graphics.FillRectangle(rectBrush, rectX, rectY, rectWidth, rectHeight);
                    }
                }

                // Конвертируем в JPEG байты
                using (var memoryStream = new MemoryStream())
                {
                    bitmap.Save(memoryStream, ImageFormat.Jpeg);
                    return memoryStream.ToArray();
                }
            }
        }

        /// <summary>
        /// Сбрасывает счетчик кадров
        /// </summary>
        public void ResetFrameCounter()
        {
            _frameCounter = 0;
        }
    }
} 