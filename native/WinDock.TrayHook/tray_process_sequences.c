// Copyright (c) 2026 WinDock contributors. MIT License.
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <stdint.h>
#include <stddef.h>
#include <string.h>
#include "tray_process_sequences.h"

// Public SDK declaration, fixed Microsoft win32metadata revision:
// 64ed9177fb76eb244fc9051c74b8a93fdfe20873, um/winternl.h.
// SystemBasicProcessInformation = 252. Only the x64 ABI is supported here.
typedef struct {
    USHORT length, maximumLength;
    PWSTR buffer;
} BasicUnicodeString;
typedef struct {
    ULONG nextEntryOffset;
    HANDLE process, parentProcess;
    ULONG64 sequence;
    BasicUnicodeString imageName;
} BasicProcessEntry;
_Static_assert(sizeof(void *) == 8, "process sequence query is x64 only");
_Static_assert(sizeof(BasicProcessEntry) == 48, "public basic process entry ABI");
_Static_assert(offsetof(BasicProcessEntry, nextEntryOffset) == 0, "next entry offset");
_Static_assert(offsetof(BasicProcessEntry, process) == 8, "PID offset");
_Static_assert(offsetof(BasicProcessEntry, parentProcess) == 16, "parent PID offset");
_Static_assert(offsetof(BasicProcessEntry, sequence) == 24, "sequence offset");
_Static_assert(offsetof(BasicProcessEntry, imageName) == 32, "image name offset");

#define QUERY_BYTES (1024u * 1024u)
#define MAX_ENTRIES 8192u
#define PID_SLOTS 16384u
#define MAX_PROBES 64u
typedef LONG (NTAPI *QuerySystemInformation)(ULONG, void *, ULONG, ULONG *);

struct TrayProcessSequences {
    QuerySystemInformation query;
    BYTE *scratch;
    DWORD seen[PID_SLOTS];
};

TrayProcessSequences *TrayProcessSequencesCreate(void) {
    HMODULE ntdll = GetModuleHandleW(L"ntdll.dll");
    QuerySystemInformation query = ntdll ?
        (QuerySystemInformation)(ULONG_PTR)GetProcAddress(ntdll, "NtQuerySystemInformation") : NULL;
    if (!query) return NULL;
    TrayProcessSequences *state = HeapAlloc(GetProcessHeap(), HEAP_ZERO_MEMORY, sizeof(*state));
    if (!state) return NULL;
    state->scratch = HeapAlloc(GetProcessHeap(), HEAP_ZERO_MEMORY, QUERY_BYTES);
    if (!state->scratch) { HeapFree(GetProcessHeap(), 0, state); return NULL; }
    state->query = query;
    return state;
}

void TrayProcessSequencesDestroy(TrayProcessSequences *state) {
    if (!state) return;
    if (state->scratch) {
        SecureZeroMemory(state->scratch, QUERY_BYTES);
        HeapFree(GetProcessHeap(), 0, state->scratch);
    }
    SecureZeroMemory(state, sizeof(*state));
    HeapFree(GetProcessHeap(), 0, state);
}

static BOOL RememberProcess(TrayProcessSequences *state, DWORD process, BOOL *seenZero) {
    if (!process) {
        if (*seenZero) return FALSE;
        *seenZero = TRUE; return TRUE;
    }
    DWORD hash = process;
    hash ^= hash >> 16; hash *= 0x7FEB352Du;
    hash ^= hash >> 15; hash *= 0x846CA68Bu;
    hash ^= hash >> 16;
    unsigned int slot = hash & (PID_SLOTS - 1);
    // Reject excessive collisions rather than perform unbounded UI work.
    for (unsigned int probe = 0; probe < MAX_PROBES; ++probe) {
        if (state->seen[slot] == process) return FALSE;
        if (!state->seen[slot]) { state->seen[slot] = process; return TRUE; }
        slot = (slot + 1) & (PID_SLOTS - 1);
    }
    return FALSE;
}

BOOL TrayProcessSequencesRead(TrayProcessSequences *state, const DWORD *processes,
    uint64_t *sequences, unsigned int count) {
    if (!sequences || !count || count > TRAY_SEQUENCE_CANDIDATES) return FALSE;
    ZeroMemory(sequences, count * sizeof(*sequences));
    if (!state || !state->query || !state->scratch || !processes) return FALSE;
    for (unsigned int i = 0; i < count; ++i) if (!processes[i]) return FALSE;
    uint64_t found[TRAY_SEQUENCE_CANDIDATES] = {0};
    BOOL valid = FALSE, seenZero = FALSE;
    ULONG returned = 0;
    ZeroMemory(state->seen, sizeof(state->seen));
    // There is deliberately no buffer-size retry on Explorer's UI thread.
    LONG status = state->query(252, state->scratch, QUERY_BYTES, &returned);
    if (status != 0 || returned < sizeof(BasicProcessEntry) || returned > QUERY_BYTES) goto done;
    SIZE_T offset = 0;
    for (unsigned int entries = 0; entries < MAX_ENTRIES; ++entries) {
        if (offset > returned || returned - offset < sizeof(BasicProcessEntry)) goto done;
        const BYTE *entry = state->scratch + offset;
        ULONG next = 0;
        uintptr_t process = 0;
        uint64_t sequence = 0;
        // Never interpret the parent PID, UNICODE_STRING or process name.
        memcpy(&next, entry + offsetof(BasicProcessEntry, nextEntryOffset), sizeof(next));
        memcpy(&process, entry + offsetof(BasicProcessEntry, process), sizeof(process));
        memcpy(&sequence, entry + offsetof(BasicProcessEntry, sequence), sizeof(sequence));
        if (process > UINT32_MAX || !RememberProcess(state, (DWORD)process, &seenZero)) goto done;
        for (unsigned int i = 0; i < count; ++i) {
            if (process == processes[i]) {
                if (!sequence) goto done;
                found[i] = sequence;
            }
        }
        if (!next) { valid = TRUE; break; }
        if (next < sizeof(BasicProcessEntry) || (next & 7u) || next > returned - offset ||
            returned - offset - next < sizeof(BasicProcessEntry)) goto done;
        offset += next;
    }
done:
    // The system response contains names even though this reader ignores them.
    // Clear the entire capacity on success and failure before returning to UI.
    SecureZeroMemory(state->scratch, QUERY_BYTES);
    SecureZeroMemory(state->seen, sizeof(state->seen));
    if (valid) CopyMemory(sequences, found, count * sizeof(*sequences));
    SecureZeroMemory(found, sizeof(found));
    return valid;
}
