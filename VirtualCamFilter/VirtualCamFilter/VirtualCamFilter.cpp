#include "pch.h"
#include "VirtualCamGuids.h"
#include "SharedMem.h"
#include <objbase.h>
#include <streams.h>
#include <ks.h>        // должно быть перед ksmedia.h, но после streams.h чтобы не перебивать константы в reftime.h
#include <ksmedia.h>   // MEDIASUBTYPE_NV12 и др.
#include <dvdmedia.h>
#include <stdio.h>
#include <atomic>

// Если в заголовках нет MEDIASUBTYPE_I420, определяем здесь
#ifndef MEDIASUBTYPE_I420
static const GUID MEDIASUBTYPE_I420 = { 0x30323449, 0x0000, 0x0010,{0x80,0x00,0x00,0xAA,0x00,0x38,0x9B,0x71} }; // 'I420'
#endif
// ----------------------------
// Запись лога в файл (append)
// ----------------------------
#include <mutex>

static HANDLE GetLogHandle()
{
    static HANDLE hLog = INVALID_HANDLE_VALUE;
    if (hLog != INVALID_HANDLE_VALUE)
        return hLog;

    const char* const fixedDir = "C:\\cwl\\VirtualCamFilter\\";
    char path[MAX_PATH] = {};
    strcpy_s(path, fixedDir);
    strcat_s(path, "vCamLog.txt");

    // Создаём каталог, если его нет
    CreateDirectoryA(fixedDir, NULL);

    hLog = CreateFileA(path, FILE_APPEND_DATA, FILE_SHARE_READ, NULL,
                       OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, NULL);
    if (hLog != INVALID_HANDLE_VALUE)
    {
        // Перемещаемся в конец
        SetFilePointer(hLog, 0, NULL, FILE_END);
    }
    return hLog;
}

static void WriteLogFile(const char* text)
{
    HANDLE h = GetLogHandle();
    if (h == INVALID_HANDLE_VALUE) return;
    DWORD len = (DWORD)strlen(text);
    DWORD written;
    WriteFile(h, text, len, &written, NULL);
}
// ----------------------------------------------------------------------------
// Утилиты для отладочного логирования
// ----------------------------------------------------------------------------
static const char* GuidName(const GUID& g)
{
    if (g == MEDIASUBTYPE_YUY2)  return "YUY2";
    if (g == MEDIASUBTYPE_RGB24) return "RGB24";
    if (g == MEDIASUBTYPE_NV12)  return "NV12";
    if (g == MEDIASUBTYPE_I420)  return "I420";
#ifdef MEDIASUBTYPE_MJPG
    if (g == MEDIASUBTYPE_MJPG)  return "MJPG";
#endif
    return "UNKNOWN";
}

static void LogMediaType(const char* where, const AM_MEDIA_TYPE* pmt)
{
    if (!pmt) return;
    const VIDEOINFOHEADER* vih = reinterpret_cast<const VIDEOINFOHEADER*>(pmt->pbFormat);
    if (!vih) return;

    char buf[256] = {};
    double fps = vih->AvgTimePerFrame ? 1e7 / static_cast<double>(vih->AvgTimePerFrame) : 0.0;
    sprintf_s(buf, "vCam %s: subtype=%s, %dx%d%s, fps=%.2f\n",
              where,
              GuidName(pmt->subtype),
              vih->bmiHeader.biWidth,
              abs(vih->bmiHeader.biHeight),
              (vih->bmiHeader.biHeight < 0 ? " top-down" : ""),
              fps);
    OutputDebugStringA(buf);
    WriteLogFile(buf);
}

static void LogMediaType(const char* where, const CMediaType* pmt)
{
    LogMediaType(where, static_cast<const AM_MEDIA_TYPE*>(pmt));
}
// ---- Описание пина/фильтра для регистрации ----
const AMOVIESETUP_MEDIATYPE sudPinTypes[] =
{
    { &MEDIATYPE_Video, &MEDIASUBTYPE_YUY2 },
    { &MEDIATYPE_Video, &MEDIASUBTYPE_NV12 },
    { &MEDIATYPE_Video, &MEDIASUBTYPE_I420 },
    { &MEDIATYPE_Video, &MEDIASUBTYPE_RGB24 }
};

const AMOVIESETUP_PIN sudPins[] =
{
    {
        L"Output",      // strName
        FALSE,           // bRendered
        TRUE,            // bOutput
        FALSE,           // bZero
        FALSE,           // bMany
        nullptr,         // clsConnectsToFilter
        nullptr,         // strConnectsToPin
        4,               // nTypes
        sudPinTypes      // lpTypes
    }
};

const AMOVIESETUP_FILTER sudFilter =
{
    &CLSID_VCam,            // clsID
    VCAM_NAME,              // strName
    MERIT_DO_NOT_USE + 1,   // dwMerit
    1,                      // nPins
    sudPins                 // lpPin
};

// ------------------------------------------------------------
// Поток, выдающий кадры
// ------------------------------------------------------------
class CPushPinVCam : public CSourceStream, public IAMStreamConfig, public IKsPropertySet
{
    SharedMem m_shm;
    LONG      m_lastId = -1;
    enum Format { NV12, I420, YUY2, RGB24 } m_format = NV12; // текущий формат
    int             m_outW  = FRAME_W;         // запрошенная ширина
    int             m_outH  = FRAME_H;         // запрошенная высота
    REFERENCE_TIME  m_rtSampleTime = 0;         // текущее время кадра
    const REFERENCE_TIME m_rtFrameLength = 333333; // 30 fps (100-нс)
    CMediaType m_mt; // For IAMStreamConfig

public:
    CPushPinVCam(HRESULT* phr, CSource* pSrc)
        : CSourceStream(NAME("vCamPin"), phr, pSrc, L"Out")
    {
        // Пытаемся открыть shared memory
        bool opened = m_shm.Open();
        char buf[128] = {};
        sprintf_s(buf, "vCam: Инициализация пина, SharedMem открыта: %s\n", opened ? "да" : "нет");
        WriteLogFile(buf);

        // Устанавливаем последний ID в недопустимое значение, 
        // чтобы гарантировать получение первого кадра
        m_lastId = -1;

        // Инициализируем базовый медиа-тип, чтобы GetMediaType() без индекса
        // мог вернуть валидное значение до того, как приложение вызовет
        // SetFormat(). По-умолчанию используем первый формат – 1920×1080 YUY2 30 fps.
        GetMediaType(0, &m_mt);

        // Обновляем внутренние параметры ширины/высоты под этот формат
        VIDEOINFOHEADER* vihDef = reinterpret_cast<VIDEOINFOHEADER*>(m_mt.Format());
        if (vihDef)
        {
            m_outW = vihDef->bmiHeader.biWidth;
            m_outH = abs(vihDef->bmiHeader.biHeight);
            m_format = YUY2;
        }
    }

    // IUnknown (delegated to CUnknown)
    STDMETHODIMP QueryInterface(REFIID riid, void** ppv) override
    {
        if (riid == IID_IAMStreamConfig)
            return GetInterface(static_cast<IAMStreamConfig*>(this), ppv);
        if (riid == IID_IKsPropertySet)
            return GetInterface(static_cast<IKsPropertySet*>(this), ppv);
        return CSourceStream::QueryInterface(riid, ppv);
    }

    STDMETHODIMP_(ULONG) AddRef() override { return CSourceStream::AddRef(); }
    STDMETHODIMP_(ULONG) Release() override { return CSourceStream::Release(); }

    // IAMStreamConfig
    STDMETHODIMP SetFormat(AM_MEDIA_TYPE* pmt) override
    {
        if (!pmt) return E_POINTER;

        // Логируем исходный запрос Discord (до любых проверок) — это поможет понять,
        // какие именно параметры он передаёт и почему мы можем их отклонять.
        LogMediaType("SetFormat(req)", pmt);

        // Принимаем только видео-тип
        if (pmt->majortype != MEDIATYPE_Video)
            return E_INVALIDARG;

        // Поддерживаемые подтипы
        const bool isYUY2  = (pmt->subtype == MEDIASUBTYPE_YUY2);
        const bool isRGB24 = (pmt->subtype == MEDIASUBTYPE_RGB24);
        const bool isNV12  = (pmt->subtype == MEDIASUBTYPE_NV12);
        const bool isI420  = (pmt->subtype == MEDIASUBTYPE_I420);
        if (!isYUY2 && !isRGB24 && !isNV12 && !isI420)
            return E_INVALIDARG;

        // Проверяем корректность структуры VIDEOINFOHEADER
        VIDEOINFOHEADER* vih = reinterpret_cast<VIDEOINFOHEADER*>(pmt->pbFormat);
        if (!vih)
            return E_INVALIDARG;

        const int w = vih->bmiHeader.biWidth;
        const int h = abs(vih->bmiHeader.biHeight);

        // Поддерживаемые размеры кадра
        static const int sizes[][2] = { {1920,1080}, {1280,720}, {960,540}, {640,480} };
        bool validSize = false;
        for (int i = 0; i < _countof(sizes); ++i)
        {
            if (w == sizes[i][0] && h == sizes[i][1])
            {
                validSize = true;
                break;
            }
        }
        if (!validSize)
            return E_INVALIDARG;

        // Сохраняем формат, но всегда используем FULL HD разрешение
        if (isNV12) m_format = NV12;
        else if (isI420) m_format = I420;
        else if (isYUY2) m_format = YUY2;
        else m_format = RGB24;
        
        // Всегда используем FULL HD разрешение, независимо от запрошенного
        m_outW = FRAME_W;  // 1920
        m_outH = FRAME_H;  // 1080
        
        // Модифицируем запрошенный формат для установки в m_mt
        vih->bmiHeader.biWidth = FRAME_W;
        if (vih->bmiHeader.biHeight < 0)
            vih->bmiHeader.biHeight = -static_cast<LONG>(FRAME_H);  // используем явное приведение типов
        else
            vih->bmiHeader.biHeight = FRAME_H;
            
        // Обновляем размер изображения в зависимости от формата
        if (isNV12 || isI420)
            vih->bmiHeader.biSizeImage = FRAME_W * FRAME_H * 3 / 2;
        else if (isYUY2)
            vih->bmiHeader.biSizeImage = FRAME_W * FRAME_H * 2;
        else
            vih->bmiHeader.biSizeImage = FRAME_W * FRAME_H * 3;
        
        m_mt.Set(*pmt);
        
        char buf[128] = {};
        sprintf_s(buf, "vCam: Установлено Full HD разрешение: %dx%d вместо запрошенного %dx%d\n", 
                 FRAME_W, FRAME_H, w, h);
        WriteLogFile(buf);

        LogMediaType("SetFormat", pmt);
        return S_OK;
    }

    STDMETHODIMP GetFormat(AM_MEDIA_TYPE** ppmt) override
    {
        if (!ppmt) return E_POINTER;
        *ppmt = (AM_MEDIA_TYPE*)CoTaskMemAlloc(sizeof(AM_MEDIA_TYPE));
        if (!*ppmt) return E_OUTOFMEMORY;
        return CopyMediaType(*ppmt, &m_mt);
    }

    STDMETHODIMP GetNumberOfCapabilities(int* piCount, int* piSize) override
    {
        if (!piCount || !piSize) return E_POINTER;
        *piCount = 16; // 4 размера × 4 формата (NV12 / I420 / YUY2 / RGB24)
        WriteLogFile("vCam: GetNumberOfCapabilities called\n");
        *piSize  = sizeof(VIDEO_STREAM_CONFIG_CAPS);
        return S_OK;
    }

    STDMETHODIMP GetStreamCaps(int iIndex, AM_MEDIA_TYPE** ppmt, BYTE* pSCC) override
    {
        static const int sizes[][2] = { {1920,1080}, {1280,720}, {960,540}, {640,480} };
        const int sizeCount = _countof(sizes);

        char bufcap[64];
        sprintf_s(bufcap, "vCam: GetStreamCaps idx=%d\n", iIndex);
        WriteLogFile(bufcap);

        if (!ppmt || !pSCC)
            return E_POINTER;

        // Всего 12 вариантов: 0-3 – YUY2, 4-7 – RGB24, 8-11 – NV12
        if (iIndex < 0 || iIndex >= sizeCount * 4)
            return S_FALSE;

        int idxGroup = iIndex / sizeCount; // 0=YUY2,1=NV12,2=I420,3=RGB24
        const int idx   = iIndex % sizeCount;
        const bool yuy2  = (idxGroup == 0);
        const bool nv12  = (idxGroup == 1);
        const bool i420  = (idxGroup == 2);
        const bool rgb24 = (idxGroup == 3);

        const int w = sizes[idx][0];
        const int h = sizes[idx][1];

        CMediaType tmp;
        VIDEOINFOHEADER vih = {};
        vih.bmiHeader.biSize        = sizeof(BITMAPINFOHEADER);
        vih.bmiHeader.biWidth       = w;
        vih.bmiHeader.biHeight      = yuy2 ? -(LONG)h : h; // YUY2 top-down, RGB24 bottom-up
        vih.bmiHeader.biPlanes      = 1;
        if (nv12)
        {
            vih.bmiHeader.biBitCount    = 12;
            vih.bmiHeader.biCompression = MAKEFOURCC('N','V','1','2');
            vih.bmiHeader.biSizeImage   = w * h * 3 / 2;
            vih.bmiHeader.biHeight      = h; // NV12 bottom-up
        }
        else if (i420)
        {
            vih.bmiHeader.biBitCount    = 12;
            vih.bmiHeader.biCompression = MAKEFOURCC('I','4','2','0');
            vih.bmiHeader.biSizeImage   = w * h * 3 / 2;
            vih.bmiHeader.biHeight      = h; // I420 – positive height
        }
        else if (yuy2)
        {
            vih.bmiHeader.biBitCount    = 16;
        vih.bmiHeader.biCompression = MAKEFOURCC('Y','U','Y','2');
            vih.bmiHeader.biSizeImage   = w * h * 2;
            vih.bmiHeader.biHeight      = -(LONG)h;
        }
        else if (rgb24)
        {
            vih.bmiHeader.biBitCount    = 24;
            vih.bmiHeader.biCompression = BI_RGB;
            vih.bmiHeader.biSizeImage   = w * h * 3;
            vih.bmiHeader.biHeight      = h;
        }

        vih.AvgTimePerFrame         = m_rtFrameLength;

        tmp.SetType(&MEDIATYPE_Video);
        if (nv12) tmp.SetSubtype(&MEDIASUBTYPE_NV12);
        else if (i420) tmp.SetSubtype(&MEDIASUBTYPE_I420);
        else if (yuy2) tmp.SetSubtype(&MEDIASUBTYPE_YUY2);
        else tmp.SetSubtype(&MEDIASUBTYPE_RGB24);
        tmp.SetFormatType(&FORMAT_VideoInfo);
        tmp.SetTemporalCompression(FALSE);
        tmp.SetSampleSize(vih.bmiHeader.biSizeImage);
        tmp.SetFormat((BYTE*)&vih, sizeof(vih));

        *ppmt = (AM_MEDIA_TYPE*)CoTaskMemAlloc(sizeof(AM_MEDIA_TYPE));
        if (!*ppmt)
            return E_OUTOFMEMORY;
        CopyMediaType(*ppmt, &tmp);

        LogMediaType("GetStreamCaps(out)", &tmp);
        return S_OK;
    }

    // No other methods

    // Настраиваем размер буфера
    HRESULT DecideBufferSize(IMemAllocator* pAlloc, ALLOCATOR_PROPERTIES* prop) override
    {
        prop->cBuffers = 1;
        switch(m_format)
        {
        case YUY2: prop->cbBuffer = m_outW * m_outH * 2; break;
        case RGB24: prop->cbBuffer = m_outW * m_outH * 3; break;
        case NV12:  prop->cbBuffer = m_outW * m_outH * 3 / 2; break;
        case I420:  prop->cbBuffer = m_outW * m_outH * 3 / 2; break;
        }
        ALLOCATOR_PROPERTIES actual = {};
        return pAlloc->SetProperties(prop, &actual);
    }

    // Запоминаем выбранный формат
    HRESULT SetMediaType(const CMediaType* pmt) override
    {
        // Логируем полученный медиатип
        LogMediaType("SetMediaType(req)", pmt);

        if (pmt->subtype == MEDIASUBTYPE_YUY2)
            m_format = YUY2;
        else if (pmt->subtype == MEDIASUBTYPE_RGB24)
            m_format = RGB24;
        else if (pmt->subtype == MEDIASUBTYPE_NV12)
            m_format = NV12;
        else if (pmt->subtype == MEDIASUBTYPE_I420)
            m_format = I420;
        else
            return E_INVALIDARG;

        VIDEOINFOHEADER* vih = reinterpret_cast<VIDEOINFOHEADER*>(pmt->pbFormat);
        if (!vih) return E_INVALIDARG;
        
        // Получаем запрошенное разрешение для логов
        const int requestedW = vih->bmiHeader.biWidth;
        const int requestedH = abs(vih->bmiHeader.biHeight);
        
        // Всегда используем FULL HD разрешение
        m_outW = FRAME_W;
        m_outH = FRAME_H;
        
        char buf[128] = {};
        sprintf_s(buf, "vCam: SetMediaType установлено Full HD: %dx%d вместо %dx%d\n", 
                 FRAME_W, FRAME_H, requestedW, requestedH);
        WriteLogFile(buf);

        m_mt.Set(*pmt);
        LogMediaType("SetMediaType", pmt);
        return CSourceStream::SetMediaType(pmt);
    }

    // Описываем выходной MediaType (RGB24, 1920x1080, 30fps)
    HRESULT GetMediaType(int iPos, CMediaType* pmt) override
    {
        if (iPos < 0) return E_INVALIDARG;

        static const int sizes[][2] = { {1920,1080}, {1280,720}, {960,540}, {640,480} };
        const int sizeCount = _countof(sizes);
        const int formatsPerSize = 4; // YUY2, NV12, I420, RGB24
        if (iPos >= sizeCount * formatsPerSize) return VFW_S_NO_MORE_ITEMS;

        int sizeIdx   = iPos / formatsPerSize;
        int formatIdx = iPos % formatsPerSize; // 0=YUY2,1=NV12,2=I420,3=RGB24

        const int w = sizes[sizeIdx][0];
        const int h = sizes[sizeIdx][1];

        VIDEOINFOHEADER* vih = reinterpret_cast<VIDEOINFOHEADER*>(pmt->AllocFormatBuffer(sizeof(VIDEOINFOHEADER)));
        ZeroMemory(vih, sizeof(VIDEOINFOHEADER));

        vih->bmiHeader.biSize   = sizeof(BITMAPINFOHEADER);
        vih->bmiHeader.biWidth  = w;
        vih->bmiHeader.biPlanes = 1;

        GUID sub;
        if (formatIdx == 0) // YUY2
        {
            vih->bmiHeader.biHeight  = h; // YUY2 bottom-up
            vih->bmiHeader.biBitCount= 16;
            vih->bmiHeader.biCompression = MAKEFOURCC('Y','U','Y','2');
            vih->bmiHeader.biSizeImage   = w*h*2;
            sub = MEDIASUBTYPE_YUY2;
        }
        else if (formatIdx == 1) // NV12
        {
            vih->bmiHeader.biHeight  = h; // NV12 bottom-up
            vih->bmiHeader.biBitCount= 12;
            vih->bmiHeader.biCompression = MAKEFOURCC('N','V','1','2');
            vih->bmiHeader.biSizeImage   = w*h*3/2;
            sub = MEDIASUBTYPE_NV12;
        }
        else if (formatIdx == 2) // I420
        {
            vih->bmiHeader.biHeight  = h; // I420
            vih->bmiHeader.biBitCount= 12;
            vih->bmiHeader.biCompression = MAKEFOURCC('I','4','2','0');
            vih->bmiHeader.biSizeImage   = w*h*3/2;
            sub = MEDIASUBTYPE_I420;
        }
        else // RGB24
        {
            vih->bmiHeader.biHeight  = h; // bottom-up
            vih->bmiHeader.biBitCount= 24;
            vih->bmiHeader.biCompression = BI_RGB;
            vih->bmiHeader.biSizeImage   = w*h*3;
            sub = MEDIASUBTYPE_RGB24;
        }

        vih->AvgTimePerFrame = m_rtFrameLength;

        pmt->SetType(&MEDIATYPE_Video);
        pmt->SetSubtype(&sub);
        pmt->SetFormatType(&FORMAT_VideoInfo);
        pmt->SetTemporalCompression(FALSE);
        pmt->SetSampleSize(vih->bmiHeader.biSizeImage);

        char buf[128];
        sprintf_s(buf, "vCam: GetMediaType iPos=%d format=%s %dx%d\n", iPos,
                  (formatIdx==0?"YUY2":(formatIdx==1?"NV12":(formatIdx==2?"I420":"RGB24"))), w, h);
        WriteLogFile(buf);

        return S_OK;
    }

    // Версия без индекса — используется базовым классом в CheckMediaType
    HRESULT GetMediaType(CMediaType* pmt) override
    {
        // Если формат уже выбран через SetFormat() – возвращаем его,
        // иначе отдаём первый из списка.
        if (m_mt.IsValid())
        {
            *pmt = m_mt;
            return S_OK;
        }

        return GetMediaType(0, pmt);
    }

    //// Записываем данные кадра в буфер семпла
    //HRESULT FillBuffer(IMediaSample* pSample) override
    //{
    //    BYTE* pData = nullptr;
    //    long  cbData = 0;
    //    pSample->GetPointer(&pData);
    //    cbData = pSample->GetSize();

    //    // Если память ещё не открыта — пытаемся открыть повторно
    //    if (!m_shm.Get())
    //    {
    //        bool opened = m_shm.Open();
    //        char buf[128] = {};
    //        sprintf_s(buf, "vCam: SharedMem open %s\n", opened ? "успешно" : "ошибка");
    //        WriteLogFile(buf);
    //    }

    //    const SharedHeader* hdr = m_shm.Get();
    //    long expectedSize;
    //    switch(m_format)
    //    {
    //    case YUY2: expectedSize = m_outW * m_outH * 2; break;
    //    case RGB24: expectedSize = m_outW * m_outH * 3; break;
    //    case NV12:  expectedSize = m_outW * m_outH * 3 / 2; break;
    //    case I420:  expectedSize = m_outW * m_outH * 3 / 2; break;
    //    }

    //    if (!hdr || cbData < expectedSize)
    //    {
    //        if (m_format == YUY2)
    //        {
    //            for (long k = 0; k < cbData; k += 4) { pData[k]=16; pData[k+1]=128; pData[k+2]=16; pData[k+3]=128; }
    //        }
    //        else if (m_format == RGB24)
    //        {
    //            ZeroMemory(pData, expectedSize);
    //        }
    //        else if (m_format == NV12)
    //        {
    //            // Y plane = 16, UV = 128
    //            memset(pData, 16, m_outW*m_outH);
    //            memset(pData + m_outW*m_outH, 128, m_outW*m_outH/2);
    //        }
    //        else if (m_format == I420)
    //        {
    //            memset(pData, 16, m_outW*m_outH); // Y
    //            memset(pData + m_outW*m_outH, 128, m_outW*m_outH/2); // U+V planes
    //        }
    //        pSample->SetActualDataLength(expectedSize);
    //        // Заполняем пустым кадром, всё равно обновляем время
    //        REFERENCE_TIME rtStart = m_rtSampleTime;
    //        REFERENCE_TIME rtEnd   = rtStart + m_rtFrameLength;
    //        pSample->SetTime(&rtStart, &rtEnd);
    //        pSample->SetSyncPoint(TRUE);
    //        m_rtSampleTime = rtEnd;
    //        return S_OK;
    //    }

    //    bool copied = false;
    //    // Проверяем, есть ли в памяти какой-либо кадр (даже если он не изменился)
    //    DWORD currentDataSize = hdr->dataSize.load(std::memory_order_acquire);
    //    if (currentDataSize == FRAME_SZ)
    //    {
    //        LONG currentFrameId = hdr->frameId.load(std::memory_order_acquire);
    //        if (currentFrameId != m_lastId) {
    //            char buf[128] = {};
    //            sprintf_s(buf, "vCam: Новый кадр, frameId=%d, предыдущий=%d\n", currentFrameId, m_lastId);
    //            WriteLogFile(buf);
    //            // Обновляем ID только когда он меняется
    //            m_lastId = currentFrameId;
    //        }
    //        const bool bottomUp = true; // мы объявили положительный biHeight, значит нижняя строка первая
    //        if (m_format == YUY2)
    //        {
    //            char buf[128] = {};
    //            sprintf_s(buf, "vCam: Конвертация в YUY2 формат, размер кадра: %dx%d\n", m_outW, m_outH);
    //            WriteLogFile(buf);
    //            
    //            // Конвертация блока RGB24 (BGR) -> YUY2, два пикселя за проход
    //            const BYTE* src = hdr->data;
    //            BYTE* dst = pData;
    //            
    //            // RGB -> YUV конверсия
    //            auto RGB2Y = [](BYTE R,BYTE G,BYTE B)->BYTE {int Y=( 66*R+129*G+25*B+128)>>8; return static_cast<BYTE>(Y+16);};
    //            auto RGB2U = [](BYTE R,BYTE G,BYTE B)->BYTE {int U=(-38*R-74*G+112*B+128)>>8;return static_cast<BYTE>(U+128);} ;
    //            auto RGB2V = [](BYTE R,BYTE G,BYTE B)->BYTE {int V=(112*R-94*G-18*B+128)>>8;return static_cast<BYTE>(V+128);} ;
    //            
    //            for (int y = 0; y < m_outH; ++y)
    //            {
    //                // Вычисляем соответствующую строку во входном изображении
    //                // с учетом масштабирования
    //                int srcY = (y * FRAME_H) / m_outH;
    //                if (bottomUp) srcY = FRAME_H - 1 - srcY;
    //                const BYTE* line = src + srcY * FRAME_W * 3;
    //                
    //                for (int x = 0; x < m_outW; x += 2)
    //                {
    //                    // Масштабируем координаты x для входного изображения
    //                    int srcX1 = (x * FRAME_W) / m_outW;
    //                    int srcX2 = ((x + 1) * FRAME_W) / m_outW;
    //                    
    //                    // первый пиксель BGR с масштабированием
    //                    BYTE B1 = line[srcX1 * 3];
    //                    BYTE G1 = line[srcX1 * 3 + 1];
    //                    BYTE R1 = line[srcX1 * 3 + 2];
    //                    
    //                    // второй пиксель BGR с масштабированием
    //                    BYTE B2 = line[srcX2 * 3];
    //                    BYTE G2 = line[srcX2 * 3 + 1];
    //                    BYTE R2 = line[srcX2 * 3 + 2];

    //                    BYTE Y1 = RGB2Y(R1,G1,B1);
    //                    BYTE Y2 = RGB2Y(R2,G2,B2);
    //                    BYTE U  = RGB2U(R1,G1,B1);
    //                    BYTE V  = RGB2V(R1,G1,B1);

    //                    *dst++ = Y1; *dst++ = U; *dst++ = Y2; *dst++ = V;
    //                }
    //            }
    //        }
    //        else if (m_format == RGB24)
    //        {
    //            // Если требуется RGB24
    //            // При запросе любого разрешения, мы всегда выдаём Full HD,
    //            // так что m_outW и m_outH всегда должны быть равны FRAME_W и FRAME_H
    //            // Но на всякий случай сохраняем проверку
    //            if (m_outW == FRAME_W && m_outH == FRAME_H)
    //            {
    //                CopyMemory(pData, hdr->data, expectedSize);
    //            }
    //            else
    //            {
    //                char buf[128] = {};
    //                sprintf_s(buf, "vCam: ВНИМАНИЕ - отличается размер кадра: %dx%d вместо %dx%d\n",
    //                         m_outW, m_outH, FRAME_W, FRAME_H);
    //                WriteLogFile(buf);
    //                
    //                // cropped top-left without scaling, bottom-up
    //                for(int y=0; y<m_outH; ++y)
    //                {
    //                    const BYTE* srcLine = hdr->data + (FRAME_H-1 - y)*FRAME_W*3;
    //                    BYTE* dstLine = pData + y*m_outW*3;
    //                    CopyMemory(dstLine, srcLine, m_outW*3);
    //                }
    //            }
    //        }
    //        else if (m_format == NV12)
    //        {
    //            char buf[128] = {};
    //            sprintf_s(buf, "vCam: Конвертация в NV12 формат, размер кадра: %dx%d\n", m_outW, m_outH);
    //            WriteLogFile(buf);
    //            
    //            // Конвертация RGB24 (BGR) -> NV12
    //            BYTE* yPlane = pData;
    //            BYTE* uvPlane = pData + m_outW * m_outH;
    //            for (int y = 0; y < m_outH; ++y)
    //            {
    //                // Масштабируем координату Y
    //                int srcY = (y * FRAME_H) / m_outH;
    //                if (bottomUp) srcY = FRAME_H - 1 - srcY;
    //                const BYTE* line = hdr->data + srcY * FRAME_W * 3;
    //                
    //                for (int x = 0; x < m_outW; ++x)
    //                {
    //                    // Масштабируем координату X
    //                    int srcX = (x * FRAME_W) / m_outW;
    //                    
    //                    BYTE B = line[srcX*3    ];
    //                    BYTE G = line[srcX*3 +1];
    //                    BYTE R = line[srcX*3 +2];

    //                    int Y  = ( 66*R +129*G + 25*B +128)>>8; // 0..255
    //                    yPlane[y*m_outW + x] = (BYTE)(Y + 16);

    //                    if ((y % 2)==0 && (x %2)==0)
    //                    {
    //                        int U = (-38*R -74*G +112*B +128)>>8;
    //                        int V = (112*R -94*G -18*B +128)>>8;
    //                        uvPlane[(y/2)*m_outW + x]     = (BYTE)(U + 128); // U
    //                        uvPlane[(y/2)*m_outW + x +1 ] = (BYTE)(V + 128); // V (next byte)
    //                    }
    //                }
    //            }
    //        }
    //        else if (m_format == I420)
    //        {
    //            char buf[128] = {};
    //            sprintf_s(buf, "vCam: Конвертация в I420 формат, размер кадра: %dx%d\n", m_outW, m_outH);
    //            WriteLogFile(buf);
    //            
    //            // Конвертация RGB24 (BGR) -> I420 (Y plane, затем U, затем V)
    //            BYTE* yPlane = pData;
    //            BYTE* uPlane = pData + m_outW * m_outH;
    //            BYTE* vPlane = uPlane + (m_outW * m_outH) / 4;

    //            for (int y = 0; y < m_outH; ++y)
    //            {
    //                // Масштабируем координату Y
    //                int srcY = (y * FRAME_H) / m_outH;
    //                if (bottomUp) srcY = FRAME_H - 1 - srcY;
    //                const BYTE* line = hdr->data + srcY * FRAME_W * 3;
    //                
    //                for (int x = 0; x < m_outW; ++x)
    //                {
    //                    // Масштабируем координату X
    //                    int srcX = (x * FRAME_W) / m_outW;
    //                    
    //                    BYTE B = line[srcX*3    ];
    //                    BYTE G = line[srcX*3 +1];
    //                    BYTE R = line[srcX*3 +2];

    //                    int Y  = ( 66*R +129*G + 25*B +128)>>8;
    //                    yPlane[y*m_outW + x] = (BYTE)(Y + 16);

    //                    if ((y % 2)==0 && (x %2)==0)
    //                    {
    //                        int U = (-38*R -74*G +112*B +128)>>8;
    //                        int V = (112*R -94*G -18*B +128)>>8;
    //                        size_t uvIndex = (y/2)*(m_outW/2) + (x/2);
    //                        uPlane[uvIndex] = (BYTE)(U + 128);
    //                        vPlane[uvIndex] = (BYTE)(V + 128);
    //                    }
    //                }
    //            }
    //        }
    //        copied = true;
    //    }

    //    if (!copied)
    //    {
    //        char buf[128] = {};
    //        // Явно загружаем атомарные значения
    //        LONG frameIdVal = hdr ? hdr->frameId.load(std::memory_order_acquire) : -1;
    //        DWORD dataSizeVal = hdr ? hdr->dataSize.load(std::memory_order_acquire) : 0;
    //        sprintf_s(buf, "vCam: Пустой кадр, hdr=%p, frameId=%d, dataSize=%d, lastId=%d\n",
    //            hdr, frameIdVal, dataSizeVal, m_lastId);
    //        WriteLogFile(buf);
    //        
    //        // Чёрный кадр
    //        if (m_format == YUY2)
    //        {
    //            for (long k = 0; k < cbData; k += 4) { pData[k]=16; pData[k+1]=128; pData[k+2]=16; pData[k+3]=128; }
    //        }
    //        else if (m_format == RGB24)
    //        {
    //            ZeroMemory(pData, expectedSize);
    //        }
    //        else if (m_format == NV12)
    //        {
    //            // Y plane = 16, UV = 128
    //            memset(pData, 16, m_outW*m_outH);
    //            memset(pData + m_outW*m_outH, 128, m_outW*m_outH/2);
    //        }
    //        else if (m_format == I420)
    //        {
    //            memset(pData, 16, m_outW*m_outH); // Y
    //            memset(pData + m_outW*m_outH, 128, m_outW*m_outH/2); // U+V planes
    //        }
    //    }

    //    // Сообщаем фактический объём данных
    //    pSample->SetActualDataLength(expectedSize);

    //    // Проставляем таймстемпы
    //    REFERENCE_TIME rtStart = m_rtSampleTime;
    //    REFERENCE_TIME rtEnd   = rtStart + m_rtFrameLength;
    //    pSample->SetTime(&rtStart, &rtEnd);
    //    pSample->SetSyncPoint(TRUE);
    //    m_rtSampleTime = rtEnd;

    //    return S_OK;
    //}
    
    // Записываем данные кадра в буфер семпла

    HRESULT FillBuffer(IMediaSample* pSample) override
    {
        BYTE* pData = nullptr;
        long  cbData = 0;
        pSample->GetPointer(&pData);
        cbData = pSample->GetSize();

        // Если память ещё не открыта — пытаемся открыть повторно
        if (!m_shm.Get())
        {
            bool opened = m_shm.Open();
            char buf[128] = {};
            sprintf_s(buf, "vCam: SharedMem open %s\n", opened ? "успешно" : "ошибка");
            WriteLogFile(buf);
        }

        const SharedHeader* hdr = m_shm.Get();
        long expectedSize;
        switch (m_format)
        {
        case YUY2: expectedSize = m_outW * m_outH * 2; break;
        case RGB24: expectedSize = m_outW * m_outH * 3; break;
        case NV12:  expectedSize = m_outW * m_outH * 3 / 2; break;
        case I420:  expectedSize = m_outW * m_outH * 3 / 2; break;
        }

        if (!hdr || cbData < expectedSize)
        {
            if (m_format == YUY2)
            {
                for (long k = 0; k < cbData; k += 4) { pData[k] = 16; pData[k + 1] = 128; pData[k + 2] = 16; pData[k + 3] = 128; }
            }
            else if (m_format == RGB24)
            {
                ZeroMemory(pData, expectedSize);
            }
            else if (m_format == NV12)
            {
                // Y plane = 16, UV = 128
                memset(pData, 16, m_outW * m_outH);
                memset(pData + m_outW * m_outH, 128, m_outW * m_outH / 2);
            }
            else if (m_format == I420)
            {
                memset(pData, 16, m_outW * m_outH); // Y
                memset(pData + m_outW * m_outH, 128, m_outW * m_outH / 2); // U+V planes
            }
            pSample->SetActualDataLength(expectedSize);
            // Заполняем пустым кадром, всё равно обновляем время
            REFERENCE_TIME rtStart = m_rtSampleTime;
            REFERENCE_TIME rtEnd = rtStart + m_rtFrameLength;
            pSample->SetTime(&rtStart, &rtEnd);
            pSample->SetSyncPoint(TRUE);
            m_rtSampleTime = rtEnd;
            return S_OK;
        }

        bool copied = false;
        // Проверяем, есть ли в памяти какой-либо кадр (даже если он не изменился)
        // Читаем индекс активного буфера атомарно
        int currentBuffer = hdr->currentBuffer.load(std::memory_order_acquire);
        DWORD currentDataSize = hdr->dataSize.load(std::memory_order_acquire);
        if (currentDataSize == FRAME_SZ)
        {
            LONG currentFrameId = hdr->frameId.load(std::memory_order_acquire);
            if (currentFrameId != m_lastId) {
                char buf[128] = {};
                sprintf_s(buf, "vCam: Новый кадр, frameId=%d, предыдущий=%d\n", currentFrameId, m_lastId);
                WriteLogFile(buf);
                // Обновляем ID только когда он меняется
                m_lastId = currentFrameId;
            }

            // Используем данные из активного буфера
            const BYTE* frameData = hdr->data[currentBuffer];
            const bool bottomUp = true;
            if (m_format == YUY2)
            {
                char buf[128] = {};
                sprintf_s(buf, "vCam: Конвертация в YUY2 формат, размер кадра: %dx%d\n", m_outW, m_outH);
                WriteLogFile(buf);

                // Конвертация блока RGB24 (BGR) -> YUY2, два пикселя за проход
                const BYTE* src = frameData;
                BYTE* dst = pData;

                // RGB -> YUV конверсия
                auto RGB2Y = [](BYTE R, BYTE G, BYTE B)->BYTE {int Y = (66 * R + 129 * G + 25 * B + 128) >> 8; return static_cast<BYTE>(Y + 16); };
                auto RGB2U = [](BYTE R, BYTE G, BYTE B)->BYTE {int U = (-38 * R - 74 * G + 112 * B + 128) >> 8; return static_cast<BYTE>(U + 128); };
                auto RGB2V = [](BYTE R, BYTE G, BYTE B)->BYTE {int V = (112 * R - 94 * G - 18 * B + 128) >> 8; return static_cast<BYTE>(V + 128); };

                for (int y = 0; y < m_outH; ++y)
                {
                    // Вычисляем соответствующую строку во входном изображении
                    // с учетом масштабирования
                    int srcY = (y * FRAME_H) / m_outH;
                    if (bottomUp) srcY = FRAME_H - 1 - srcY;
                    const BYTE* line = src + srcY * FRAME_W * 3;

                    for (int x = 0; x < m_outW; x += 2)
                    {
                        // Масштабируем координаты x для входного изображения
                        int srcX1 = (x * FRAME_W) / m_outW;
                        int srcX2 = ((x + 1) * FRAME_W) / m_outW;

                        // первый пиксель BGR с масштабированием
                        BYTE B1 = line[srcX1 * 3];
                        BYTE G1 = line[srcX1 * 3 + 1];
                        BYTE R1 = line[srcX1 * 3 + 2];

                        // второй пиксель BGR с масштабированием
                        BYTE B2 = line[srcX2 * 3];
                        BYTE G2 = line[srcX2 * 3 + 1];
                        BYTE R2 = line[srcX2 * 3 + 2];

                        BYTE Y1 = RGB2Y(R1, G1, B1);
                        BYTE Y2 = RGB2Y(R2, G2, B2);
                        BYTE U = RGB2U(R1, G1, B1);
                        BYTE V = RGB2V(R1, G1, B1);

                        *dst++ = Y1; *dst++ = U; *dst++ = Y2; *dst++ = V;
                    }
                }
            }
            else if (m_format == RGB24)
            {
                // Если требуется RGB24
                // При запросе любого разрешения, мы всегда выдаём Full HD,
                // так что m_outW и m_outH всегда должны быть равны FRAME_W и FRAME_H
                // Но на всякий случай сохраняем проверку
                if (m_outW == FRAME_W && m_outH == FRAME_H)
                {
                    CopyMemory(pData, frameData, expectedSize);
                }
                else
                {
                    char buf[128] = {};
                    sprintf_s(buf, "vCam: ВНИМАНИЕ - отличается размер кадра: %dx%d вместо %dx%d\n",
                        m_outW, m_outH, FRAME_W, FRAME_H);
                    WriteLogFile(buf);

                    // cropped top-left without scaling, bottom-up
                    for (int y = 0; y < m_outH; ++y)
                    {
                        const BYTE* srcLine = frameData + (FRAME_H - 1 - y) * FRAME_W * 3;
                        BYTE* dstLine = pData + y * m_outW * 3;
                        CopyMemory(dstLine, srcLine, m_outW * 3);
                    }
                }
            }
            else if (m_format == NV12)
            {
                char buf[128] = {};
                sprintf_s(buf, "vCam: Конвертация в NV12 формат, размер кадра: %dx%d\n", m_outW, m_outH);
                WriteLogFile(buf);

                // Конвертация RGB24 (BGR) -> NV12
                BYTE* yPlane = pData;
                BYTE* uvPlane = pData + m_outW * m_outH;
                for (int y = 0; y < m_outH; ++y)
                {
                    // Масштабируем координату Y
                    int srcY = (y * FRAME_H) / m_outH;
                    if (bottomUp) srcY = FRAME_H - 1 - srcY;
                    const BYTE* line = frameData + srcY * FRAME_W * 3;

                    for (int x = 0; x < m_outW; ++x)
                    {
                        // Масштабируем координату X
                        int srcX = (x * FRAME_W) / m_outW;

                        BYTE B = line[srcX * 3];
                        BYTE G = line[srcX * 3 + 1];
                        BYTE R = line[srcX * 3 + 2];

                        int Y = (66 * R + 129 * G + 25 * B + 128) >> 8; // 0..255
                        yPlane[y * m_outW + x] = (BYTE)(Y + 16);

                        if ((y % 2) == 0 && (x % 2) == 0)
                        {
                            int U = (-38 * R - 74 * G + 112 * B + 128) >> 8;
                            int V = (112 * R - 94 * G - 18 * B + 128) >> 8;
                            uvPlane[(y / 2) * m_outW + x] = (BYTE)(U + 128); // U
                            uvPlane[(y / 2) * m_outW + x + 1] = (BYTE)(V + 128); // V (next byte)
                        }
                    }
                }
            }
            else if (m_format == I420)
            {
                char buf[128] = {};
                sprintf_s(buf, "vCam: Конвертация в I420 формат, размер кадра: %dx%d\n", m_outW, m_outH);
                WriteLogFile(buf);

                // Конвертация RGB24 (BGR) -> I420 (Y plane, затем U, затем V)
                BYTE* yPlane = pData;
                BYTE* uPlane = pData + m_outW * m_outH;
                BYTE* vPlane = uPlane + (m_outW * m_outH) / 4;

                for (int y = 0; y < m_outH; ++y)
                {
                    // Масштабируем координату Y
                    int srcY = (y * FRAME_H) / m_outH;
                    if (bottomUp) srcY = FRAME_H - 1 - srcY;
                    const BYTE* line = frameData + srcY * FRAME_W * 3;

                    for (int x = 0; x < m_outW; ++x)
                    {
                        // Масштабируем координату X
                        int srcX = (x * FRAME_W) / m_outW;

                        BYTE B = line[srcX * 3];
                        BYTE G = line[srcX * 3 + 1];
                        BYTE R = line[srcX * 3 + 2];

                        int Y = (66 * R + 129 * G + 25 * B + 128) >> 8;
                        yPlane[y * m_outW + x] = (BYTE)(Y + 16);

                        if ((y % 2) == 0 && (x % 2) == 0)
                        {
                            int U = (-38 * R - 74 * G + 112 * B + 128) >> 8;
                            int V = (112 * R - 94 * G - 18 * B + 128) >> 8;
                            size_t uvIndex = (y / 2) * (m_outW / 2) + (x / 2);
                            uPlane[uvIndex] = (BYTE)(U + 128);
                            vPlane[uvIndex] = (BYTE)(V + 128);
                        }
                    }
                }
            }
            copied = true;
        }

        if (!copied)
        {
            char buf[128] = {};
            // Явно загружаем атомарные значения
            LONG frameIdVal = hdr ? hdr->frameId.load(std::memory_order_acquire) : -1;
            DWORD dataSizeVal = hdr ? hdr->dataSize.load(std::memory_order_acquire) : 0;
            sprintf_s(buf, "vCam: Пустой кадр, hdr=%p, frameId=%d, dataSize=%d, lastId=%d\n",
                hdr, frameIdVal, dataSizeVal, m_lastId);
            WriteLogFile(buf);

            // Чёрный кадр
            if (m_format == YUY2)
            {
                for (long k = 0; k < cbData; k += 4) { pData[k] = 16; pData[k + 1] = 128; pData[k + 2] = 16; pData[k + 3] = 128; }
            }
            else if (m_format == RGB24)
            {
                ZeroMemory(pData, expectedSize);
            }
            else if (m_format == NV12)
            {
                // Y plane = 16, UV = 128
                memset(pData, 16, m_outW * m_outH);
                memset(pData + m_outW * m_outH, 128, m_outW * m_outH / 2);
            }
            else if (m_format == I420)
            {
                memset(pData, 16, m_outW * m_outH); // Y
                memset(pData + m_outW * m_outH, 128, m_outW * m_outH / 2); // U+V planes
            }
        }

        // Сообщаем фактический объём данных
        pSample->SetActualDataLength(expectedSize);

        // Проставляем таймстемпы
        REFERENCE_TIME rtStart = m_rtSampleTime;
        REFERENCE_TIME rtEnd = rtStart + m_rtFrameLength;
        pSample->SetTime(&rtStart, &rtEnd);
        pSample->SetSyncPoint(TRUE);
        m_rtSampleTime = rtEnd;

        return S_OK;
    }

    // IKsPropertySet (support only PIN_CATEGORY)
    STDMETHODIMP Set(REFGUID guidPropSet, DWORD dwID, void* pInstanceData, DWORD cbInstanceData, void* pPropData, DWORD cbPropData) override
    {
        UNREFERENCED_PARAMETER(guidPropSet);
        UNREFERENCED_PARAMETER(dwID);
        UNREFERENCED_PARAMETER(pInstanceData);
        UNREFERENCED_PARAMETER(cbInstanceData);
        UNREFERENCED_PARAMETER(pPropData);
        UNREFERENCED_PARAMETER(cbPropData);
        return E_NOTIMPL;
    }

    STDMETHODIMP Get(REFGUID guidPropSet, DWORD dwPropID, void* pInstanceData, DWORD cbInstanceData, void* pPropData, DWORD cbPropData, DWORD* pcbReturned) override
    {
        if (guidPropSet != AMPROPSETID_Pin)
            return E_PROP_SET_UNSUPPORTED;
        if (dwPropID != AMPROPERTY_PIN_CATEGORY)
            return E_PROP_ID_UNSUPPORTED;
        if (pPropData == nullptr && pcbReturned == nullptr)
            return E_POINTER;

        if (pcbReturned)
            *pcbReturned = sizeof(GUID);
        if (pPropData == nullptr)
            return S_OK;
        if (cbPropData < sizeof(GUID))
            return E_UNEXPECTED;

        *(GUID*)pPropData = PIN_CATEGORY_CAPTURE;
        return S_OK;
    }

    STDMETHODIMP QuerySupported(REFGUID guidPropSet, DWORD dwPropID, DWORD* pTypeSupport) override
    {
        if (guidPropSet != AMPROPSETID_Pin || dwPropID != AMPROPERTY_PIN_CATEGORY)
            return E_PROP_ID_UNSUPPORTED;
        if (pTypeSupport)
            *pTypeSupport = KSPROPERTY_SUPPORT_GET;
        return S_OK;
    }
};

// ------------------------------------------------------------
// Источник (фильтр), владеет одним пином
// ------------------------------------------------------------
class CVCamSource : public CSource, public IAMFilterMiscFlags
{
public:
    CVCamSource(IUnknown* pUnk, HRESULT* phr)
        : CSource(NAME("vCamSource"), pUnk, CLSID_VCam)
    {
        m_paStreams = (CSourceStream**)new CPushPinVCam*[1];
        m_paStreams[0] = new CPushPinVCam(phr, this);
        m_iPins = 1;
    }

    static CUnknown* WINAPI CreateInstance(IUnknown* pUnk, HRESULT* phr)
    {
        return new CVCamSource(pUnk, phr);
    }

    // IUnknown delegation
    STDMETHODIMP QueryInterface(REFIID riid, void** ppv) override
    {
        if (riid == IID_IAMFilterMiscFlags)
            return GetInterface(static_cast<IAMFilterMiscFlags*>(this), ppv);
        return CSource::QueryInterface(riid, ppv);
    }

    STDMETHODIMP_(ULONG) AddRef() override { return CSource::AddRef(); }
    STDMETHODIMP_(ULONG) Release() override { return CSource::Release(); }

    // IAMFilterMiscFlags
    ULONG STDMETHODCALLTYPE GetMiscFlags() override { return AM_FILTER_MISC_FLAGS_IS_SOURCE; }
};

// ------------------------------------------------------------
// COM фабрика и регистрация
// ------------------------------------------------------------
CFactoryTemplate g_Templates[] =
{
    { VCAM_NAME, &CLSID_VCam, CVCamSource::CreateInstance, nullptr, &sudFilter }
};
int g_cTemplates = ARRAYSIZE(g_Templates);

// Экспортируемые функции (реализованы в baseclasses)
extern "C" BOOL WINAPI DllEntryPoint(HINSTANCE, ULONG, LPVOID);
BOOL APIENTRY DllMain(HMODULE hModule, DWORD fdwReason, LPVOID)
{
    char procBuf[260] = {};
    DWORD pid = GetCurrentProcessId();
    char name[MAX_PATH] = {};
    if (GetModuleFileNameA(NULL, name, MAX_PATH))
    {
        const char* base = strrchr(name, '\\');
        base = base ? base + 1 : name;
        sprintf_s(procBuf, "vCam: %s (PID %lu) %s\n", base, pid,
                  (fdwReason == DLL_PROCESS_ATTACH ? "DLL_PROCESS_ATTACH" : (fdwReason==DLL_PROCESS_DETACH?"DLL_PROCESS_DETACH":"OTHER")));
        WriteLogFile(procBuf);
    }

    return DllEntryPoint((HINSTANCE)hModule, fdwReason, nullptr);
}

STDAPI DllRegisterServer(void)
{
    // Сначала стандартная регистрация (CLSID + InprocServer32 и т.д.)
    HRESULT hr = AMovieDllRegisterServer2(TRUE);
    if (FAILED(hr)) return hr;

    // Регистрируем фильтр в категории VideoInputDevice
    IFilterMapper2* pFM = nullptr;
    hr = CoCreateInstance(CLSID_FilterMapper2, nullptr, CLSCTX_INPROC_SERVER,
                          IID_IFilterMapper2, reinterpret_cast<void**>(&pFM));
    if (FAILED(hr)) return hr;

    REGFILTER2 rf2 = {};
    rf2.dwVersion = 1;
    rf2.dwMerit   = sudFilter.dwMerit;
    rf2.cPins     = sudFilter.nPins;
    rf2.rgPins    = sudPins;

    hr = pFM->RegisterFilter(CLSID_VCam, VCAM_NAME, nullptr,
                             &CLSID_VideoInputDeviceCategory, nullptr, &rf2);
    pFM->Release();
    return hr;
}

STDAPI DllUnregisterServer(void)
{
    // Снимаем регистрацию из категории VideoInputDevice
    IFilterMapper2* pFM = nullptr;
    HRESULT hr = CoCreateInstance(CLSID_FilterMapper2, nullptr, CLSCTX_INPROC_SERVER,
                                  IID_IFilterMapper2, reinterpret_cast<void**>(&pFM));
    if (SUCCEEDED(hr))
    {
        pFM->UnregisterFilter(&CLSID_VideoInputDeviceCategory, nullptr, CLSID_VCam);
        pFM->Release();
    }

    // Удаляем остальные записи (CLSID, пины и т.д.)
    return AMovieDllRegisterServer2(FALSE);
} 