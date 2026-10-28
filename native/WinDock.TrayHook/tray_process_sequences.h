// Copyright (c) 2026 WinDock contributors. MIT License.
#ifndef WINDOCK_TRAY_PROCESS_SEQUENCES_H
#define WINDOCK_TRAY_PROCESS_SEQUENCES_H
#include <windows.h>
#include <stdint.h>

#define TRAY_SEQUENCE_CANDIDATES 2u
typedef struct TrayProcessSequences TrayProcessSequences;

// Worker only. Resolve the public ntdll export and allocate all query storage.
TrayProcessSequences *TrayProcessSequencesCreate(void);
void TrayProcessSequencesDestroy(TrayProcessSequences *state);

// One synchronous kernel query, at most 1 MiB / 8192 entries. No allocation,
// retries, resizing, COM or waiting for a worker. This is bounded work, not a
// hard execution-time guarantee. The caller serializes access and destruction.
// Only the requested PID -> sequence pairs leave the zeroed scratch storage.
BOOL TrayProcessSequencesRead(TrayProcessSequences *state, const DWORD *processes,
    uint64_t *sequences, unsigned int count);
#endif
