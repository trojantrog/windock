// Copyright (c) 2026 WinDock contributors. MIT License.
// Public WinRT ABI declarations used by this C-only component. Method order,
// types and IIDs are from Microsoft's SDK IDL at win32metadata commit
// 64ed9177fb76eb244fc9051c74b8a93fdfe20873:
// generation/WinSDK/RecompiledIdlHeaders/winrt/windows.graphics.imaging.idl
// generation/WinSDK/RecompiledIdlHeaders/winrt/windows.storage.streams.idl
// The SDK's robuffer/objidlbase declarations provide the remaining interfaces.
#ifndef WINDOCK_TRAY_IMAGE_ABI_H
#define WINDOCK_TRAY_IMAGE_ABI_H
#include <windows.h>
#include <roapi.h>
#include <winstring.h>
#include <inspectable.h>

typedef struct WdBuffer WdBuffer;
typedef struct WdBufferFactory WdBufferFactory;
typedef struct WdSoftwareBitmap WdSoftwareBitmap;
typedef struct WdBufferByteAccess WdBufferByteAccess;

#define WD_INSPECTABLE_METHODS(T) \
    HRESULT (STDMETHODCALLTYPE *QueryInterface)(T *, REFIID, void **); \
    ULONG (STDMETHODCALLTYPE *AddRef)(T *); \
    ULONG (STDMETHODCALLTYPE *Release)(T *); \
    HRESULT (STDMETHODCALLTYPE *GetIids)(T *, ULONG *, IID **); \
    HRESULT (STDMETHODCALLTYPE *GetRuntimeClassName)(T *, HSTRING *); \
    HRESULT (STDMETHODCALLTYPE *GetTrustLevel)(T *, TrustLevel *)

typedef struct {
    WD_INSPECTABLE_METHODS(WdBuffer);
    HRESULT (STDMETHODCALLTYPE *get_Capacity)(WdBuffer *, UINT32 *);
    HRESULT (STDMETHODCALLTYPE *get_Length)(WdBuffer *, UINT32 *);
    HRESULT (STDMETHODCALLTYPE *put_Length)(WdBuffer *, UINT32);
} WdBufferVtbl;
struct WdBuffer { const WdBufferVtbl *lpVtbl; };

typedef struct {
    WD_INSPECTABLE_METHODS(WdBufferFactory);
    HRESULT (STDMETHODCALLTYPE *Create)(WdBufferFactory *, UINT32, WdBuffer **);
} WdBufferFactoryVtbl;
struct WdBufferFactory { const WdBufferFactoryVtbl *lpVtbl; };

typedef struct {
    HRESULT (STDMETHODCALLTYPE *QueryInterface)(WdBufferByteAccess *, REFIID, void **);
    ULONG (STDMETHODCALLTYPE *AddRef)(WdBufferByteAccess *);
    ULONG (STDMETHODCALLTYPE *Release)(WdBufferByteAccess *);
    HRESULT (STDMETHODCALLTYPE *Buffer)(WdBufferByteAccess *, BYTE **);
} WdBufferByteAccessVtbl;
struct WdBufferByteAccess { const WdBufferByteAccessVtbl *lpVtbl; };

typedef struct {
    WD_INSPECTABLE_METHODS(WdSoftwareBitmap);
    HRESULT (STDMETHODCALLTYPE *get_BitmapPixelFormat)(WdSoftwareBitmap *, INT32 *);
    HRESULT (STDMETHODCALLTYPE *get_BitmapAlphaMode)(WdSoftwareBitmap *, INT32 *);
    HRESULT (STDMETHODCALLTYPE *get_PixelWidth)(WdSoftwareBitmap *, INT32 *);
    HRESULT (STDMETHODCALLTYPE *get_PixelHeight)(WdSoftwareBitmap *, INT32 *);
    HRESULT (STDMETHODCALLTYPE *get_IsReadOnly)(WdSoftwareBitmap *, BYTE *);
    HRESULT (STDMETHODCALLTYPE *put_DpiX)(WdSoftwareBitmap *, double);
    HRESULT (STDMETHODCALLTYPE *get_DpiX)(WdSoftwareBitmap *, double *);
    HRESULT (STDMETHODCALLTYPE *put_DpiY)(WdSoftwareBitmap *, double);
    HRESULT (STDMETHODCALLTYPE *get_DpiY)(WdSoftwareBitmap *, double *);
    HRESULT (STDMETHODCALLTYPE *LockBuffer)(WdSoftwareBitmap *, INT32, void **);
    HRESULT (STDMETHODCALLTYPE *CopyTo)(WdSoftwareBitmap *, WdSoftwareBitmap *);
    HRESULT (STDMETHODCALLTYPE *CopyFromBuffer)(WdSoftwareBitmap *, WdBuffer *);
    HRESULT (STDMETHODCALLTYPE *CopyToBuffer)(WdSoftwareBitmap *, WdBuffer *);
    HRESULT (STDMETHODCALLTYPE *GetReadOnlyView)(WdSoftwareBitmap *, WdSoftwareBitmap **);
} WdSoftwareBitmapVtbl;
struct WdSoftwareBitmap { const WdSoftwareBitmapVtbl *lpVtbl; };
#undef WD_INSPECTABLE_METHODS

static const IID WD_IID_SOFTWARE_BITMAP = {0x689E0708,0x7EEF,0x483F,{0x96,0x3F,0xDA,0x93,0x88,0x18,0xE0,0x73}};
static const IID WD_IID_BUFFER_FACTORY = {0x71AF914D,0xC10F,0x484B,{0xBC,0x50,0x14,0xBC,0x62,0x3B,0x3A,0x27}};
static const IID WD_IID_BUFFER_BYTES = {0x905A0FEF,0xBC53,0x11DF,{0x8C,0x49,0x00,0x1E,0x4F,0xC6,0x86,0xDA}};
static const IID WD_IID_AGILE = {0x94EA2B94,0xE9CC,0x49E0,{0xC0,0xFF,0xEE,0x64,0xCA,0x8F,0x5B,0x90}};
_Static_assert(offsetof(WdSoftwareBitmapVtbl, CopyToBuffer) == 18 * sizeof(void *), "SDK SoftwareBitmap ABI");
_Static_assert(offsetof(WdBufferFactoryVtbl, Create) == 6 * sizeof(void *), "SDK BufferFactory ABI");
#endif
