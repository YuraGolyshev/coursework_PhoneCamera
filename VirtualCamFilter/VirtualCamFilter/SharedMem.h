#pragma once
#include <windows.h>
#include <atomic>

// Константы Full HD кадра (BGR24)
constexpr DWORD FRAME_W   = 1920;
constexpr DWORD FRAME_H   = 1080;
constexpr DWORD FRAME_BPP = 3;        // BGR24
constexpr DWORD FRAME_SZ  = FRAME_W * FRAME_H * FRAME_BPP; // 6 220 800 байт

// Структура, лежащая в разделяемой памяти
struct SharedHeader
{
    //std::atomic<LONG> frameId;   // Атомарный ID кадра // инкремент при каждом новом кадре
    //std::atomic<DWORD> dataSize; // Атомарный размер данных // должно быть FRAME_SZ
    //BYTE data[FRAME_SZ];
    std::atomic<LONG> frameId;
    std::atomic<DWORD> dataSize;
    std::atomic<int> currentBuffer; // 0 или 1
    BYTE data[2][FRAME_SZ];        // Два буфера
};

// Класс-обёртка для доступа (только чтение) к shared memory
class SharedMem
{
    HANDLE hMap  = nullptr;
    SharedHeader* pMem = nullptr;
    int openAttempts = 0;

public:
    bool Open()
    {
        if (hMap) return true;
        
        // Пытаемся несколько раз, с небольшими задержками
        for (int i = 0; i < 3; i++)
        {
            hMap = ::OpenFileMappingW(FILE_MAP_READ, FALSE, L"Global\\vCamShm");
            if (hMap) break;
            ::Sleep(100); // Ждем 100 мс перед следующей попыткой
        }
        
        openAttempts++;
        
        if (!hMap) return false;
        pMem = reinterpret_cast<SharedHeader*>(::MapViewOfFile(hMap, FILE_MAP_READ, 0, 0, sizeof(SharedHeader)));
        return pMem != nullptr;
    }

    const SharedHeader* Get() const { return pMem; }

    ~SharedMem()
    {
        if (pMem) ::UnmapViewOfFile(pMem);
        if (hMap) ::CloseHandle(hMap);
    }
}; 