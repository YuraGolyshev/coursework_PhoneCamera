#pragma once
#include <initguid.h>

// {B9F67B3D-1234-45FA-9F71-73E5EAA4C001}
// Уникальный CLSID для нашей виртуальной камеры
DEFINE_GUID(CLSID_VCam,
0xb9f67b3d, 0x1234, 0x45fa, 0x9f, 0x71, 0x73, 0xe5, 0xea, 0xa4, 0xc0, 0x01);

// Человекочитаемое имя устройства, которое увидит система
#define VCAM_NAME  L"Phone Camera" 