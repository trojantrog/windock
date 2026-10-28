#ifndef WINDOCK_TRAY_IMAGE_VERIFY_H
#define WINDOCK_TRAY_IMAGE_VERIFY_H

#include <windows.h>
#include <stdint.h>

typedef struct TrayImageProfile TrayImageProfile;

/* Worker only. Holds the already-loaded, verified Taskbar module until Destroy.
 * Publication to the UI thread requires the caller's synchronization. */
TrayImageProfile *TrayImageProfileCreate(void);

/* UI-safe bounded memory comparison: no disk I/O, heap allocation or waiting
 * for a worker. The caller must keep the immutable profile alive throughout. */
BOOL TrayImageProfileValidateLive(const TrayImageProfile *profile);
uintptr_t TrayImageProfileBase(const TrayImageProfile *profile);

/* Worker only, after no UI acquisition can still access this profile. */
void TrayImageProfileDestroy(TrayImageProfile *profile);

/* Copies only this process's memory, at most 4096 bytes per call. Exact reads
 * are required; this does not establish ownership or object lifetime. */
BOOL TrayImageReadMemory(uintptr_t address, void *output, SIZE_T bytes);

#endif
