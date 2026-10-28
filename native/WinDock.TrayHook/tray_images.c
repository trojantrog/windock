// Copyright (c) 2026 WinDock contributors. MIT License.
// Current Explorer SoftwareBitmap images. Private object access is confined to
// the verified icon thread; only an owned, agile bitmap reaches the MTA worker.
#define WIN32_LEAN_AND_MEAN
#ifndef _WIN32_WINNT
#define _WIN32_WINNT 0x0A00
#endif
#include <windows.h>
#include <stdint.h>
#include <stddef.h>
#include <string.h>
#include "tray_images.h"
#include "tray_image_abi.h"
#include "tray_image_profile.h"
#include "tray_image_verify.h"
#include "tray_process_sequences.h"

#define IMAGE_JOBS 2u
#define MAX_MODEL_ICONS 512u

typedef struct {
    WdSoftwareBitmap *bitmap;
    TrayImageBody body;
    uint64_t epoch;
} ImageJob;

typedef struct {
    HMODULE module;
    HANDLE wake;
    HWND tray;
    DWORD host, nonce;
    uint64_t epoch;
    TrayImageProfile *profile;
    TrayProcessSequences *processSequences;
    ImageJob jobs[IMAGE_JOBS];
    unsigned int count, cursor;
    uintptr_t lastManager;
    DWORD sampled, published, roundStatus;
    BOOL ready, profileRejected, pixelsRejected, inFlight, ending;
} ImageEngine;

static SRWLOCK imageLock = SRWLOCK_INIT;
static ImageEngine *engine;
static volatile LONG activeNonce;
static HMODULE imageModule;
static volatile LONG statisticsNonce, statisticsSampled, statisticsPublished;

static BOOL Active(DWORD nonce) {
    return nonce && (DWORD)InterlockedCompareExchange(&activeNonce, 0, 0) == nonce;
}

static void Report(DWORD nonce, DWORD status, DWORD sampled, DWORD published) {
    if ((DWORD)InterlockedCompareExchange(&statisticsNonce, 0, 0) == nonce) {
        DWORD currentSampled = (DWORD)InterlockedCompareExchange(&statisticsSampled, 0, 0);
        DWORD currentPublished = (DWORD)InterlockedCompareExchange(&statisticsPublished, 0, 0);
        if ((DWORD)InterlockedCompareExchange(&statisticsNonce, 0, 0) == nonce) {
            sampled = currentSampled; published = currentPublished;
        }
    }
    TrayImageStatus message = {nonce, status, TrayHookNextSequence(), sampled, published};
    TrayHookPublishImageStatus(message);
}

static void ResetStatistics(DWORD nonce) {
    InterlockedExchange(&statisticsNonce, 0);
    InterlockedExchange(&statisticsSampled, 0);
    InterlockedExchange(&statisticsPublished, 0);
    InterlockedExchange(&statisticsNonce, (LONG)nonce);
}

static BOOL ReadPointer(uintptr_t address, uintptr_t *value) {
    return !(address & (sizeof(uintptr_t) - 1)) &&
        TrayImageReadMemory(address, value, sizeof(*value)) && *value >= 0x10000 &&
        !(*value & (sizeof(uintptr_t) - 1));
}

static BOOL SameVtable(uintptr_t object, uintptr_t expected) {
    uintptr_t table = 0;
    return ReadPointer(object, &table) && table == expected;
}

static BOOL OnIconThread(uintptr_t base, HWND tray) {
    DWORD stored = 0, process = 0;
    DWORD current = GetCurrentThreadId(), owner = GetWindowThreadProcessId(tray, &process);
    return current && process == GetCurrentProcessId() && owner == current &&
        TrayImageReadMemory(base + tray_image_layout.uiThreadRva, &stored, sizeof(stored)) && stored == current;
}

static BOOL BitmapGeometry(WdSoftwareBitmap *bitmap, TrayImageBody *body) {
    INT32 width = 0, height = 0, format = 0, alpha = -1;
    if (FAILED(bitmap->lpVtbl->get_PixelWidth(bitmap, &width)) ||
        FAILED(bitmap->lpVtbl->get_PixelHeight(bitmap, &height)) ||
        FAILED(bitmap->lpVtbl->get_BitmapPixelFormat(bitmap, &format)) ||
        FAILED(bitmap->lpVtbl->get_BitmapAlphaMode(bitmap, &alpha)) ||
        width <= 0 || height <= 0 || width > (INT32)TRAY_IMAGE_MAX_DIMENSION ||
        height > (INT32)TRAY_IMAGE_MAX_DIMENSION || format != 87 || alpha != 0) return FALSE;
    body->pixelFormat = 1; // BGRA8 with premultiplied alpha, verified above.
    body->width = (DWORD)width; body->height = (DWORD)height;
    body->stride = (DWORD)width * 4u;
    body->pixelBytes = body->stride * (DWORD)height;
    return body->pixelBytes && body->pixelBytes <= TRAY_IMAGE_MAX_BYTES;
}

// A denied process query yields only a pending sequence candidate, with a zero
// instanceValue. It is not an identity until both fresh batch queries agree.
static BOOL ProbeOwner(uintptr_t item, TrayImageBody *body) {
    uintptr_t window = 0;
    if (!TrayImageReadMemory(item + tray_image_layout.itemWindowOffset, &window, sizeof(window)) ||
        !TrayImageReadMemory(item + tray_image_layout.itemIdOffset, &body->id, sizeof(body->id)) ||
        !TrayImageReadMemory(item + tray_image_layout.itemGuidOffset, &body->guid, sizeof(body->guid))) return FALSE;
    HWND hwnd = (HWND)window;
    DWORD pid = 0, tid = GetWindowThreadProcessId(hwnd, &pid);
    if (!hwnd || !tid || !pid || !IsWindow(hwnd)) return FALSE;
    body->window = (uint64_t)window; body->process = pid; body->thread = tid;
    HANDLE process = pid == GetCurrentProcessId() ? GetCurrentProcess() :
        OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, FALSE, pid);
    if (!process) {
        if (GetLastError() != ERROR_ACCESS_DENIED) return FALSE;
        // This entry came from the verified current Explorer model, not IPC.
        // ProcessIdToSessionId also requires owner query permission. Do not
        // pretend that a sequence result contains an independently read session.
        body->instanceKind = TRAY_INSTANCE_SYSTEM_SEQUENCE;
        body->instanceValue = 0;
        return TRUE;
    }
    DWORD ownSession = 0, ownerSession = 0;
    FILETIME started, exited, kernel, user;
    BOOL ok = ProcessIdToSessionId(GetCurrentProcessId(), &ownSession) &&
        ProcessIdToSessionId(pid, &ownerSession) && ownSession == ownerSession &&
        GetProcessTimes(process, &started, &exited, &kernel, &user);
    if (pid != GetCurrentProcessId()) CloseHandle(process);
    if (!ok) return FALSE;
    body->instanceKind = TRAY_INSTANCE_CREATION_TIME;
    body->instanceValue = ((uint64_t)started.dwHighDateTime << 32) | started.dwLowDateTime;
    return body->instanceValue != 0;
}

static BOOL SameWindowOwner(const TrayImageBody *body) {
    DWORD pid = 0;
    DWORD tid = GetWindowThreadProcessId((HWND)(uintptr_t)body->window, &pid);
    return tid && tid == body->thread && pid == body->process && IsWindow((HWND)(uintptr_t)body->window);
}

static BOOL MatchesOwnerProbe(const TrayImageBody *expected, const TrayImageBody *probe) {
    if (!expected->instanceValue || expected->window != probe->window || expected->process != probe->process ||
        expected->thread != probe->thread || expected->id != probe->id ||
        expected->instanceKind != probe->instanceKind || memcmp(&expected->guid, &probe->guid, sizeof(GUID))) return FALSE;
    if (expected->instanceKind == TRAY_INSTANCE_CREATION_TIME)
        return expected->instanceValue == probe->instanceValue;
    // These candidates must still pass a second, independent sequence query
    // after every getter has finished and before any job reaches the worker.
    return expected->instanceKind == TRAY_INSTANCE_SYSTEM_SEQUENCE && probe->instanceValue == 0;
}

// The caller holds imageLock only after a successful TryAcquire on the UI
// thread. The worker never holds this lock across file, COM or pipe operations.
static DWORD CaptureItem(ImageEngine *state, uintptr_t item, const TrayImageBody *expected, ImageJob *job) {
    uintptr_t base = TrayImageProfileBase(state->profile);
    uintptr_t iface = item + tray_image_layout.itemInterfaceOffset;
    if (iface < item || !SameVtable(item, base + tray_image_layout.itemVtableRva) ||
        !SameVtable(iface, base + tray_image_layout.itemInterfaceVtableRva)) return 5;
    TrayImageBody before = {0};
    if (!ProbeOwner(item, &before) || !MatchesOwnerProbe(expected, &before)) return 5;
    TrayImageBody identity = *expected;
    uint64_t mutation = TrayHookCurrentMutation();
    IUnknown *image = NULL, *agile = NULL;
    WdSoftwareBitmap *bitmap = NULL;
    DWORD status = 5;
    // The complete table and this method's code were checked immediately before
    // traversal. The method returns an independent AddRef, never a borrowed ptr.
    const void *const *table = *(const void *const **)iface;
    typedef HRESULT (STDMETHODCALLTYPE *GetIcon)(void *, void **);
    GetIcon getIcon = (GetIcon)table[tray_image_layout.itemIconSlot];
    // The preceding public API calls, or a previous candidate in this batch,
    // may have dispatched nested work. Never use the private fail-fast getter
    // itself to test whether the owning UI thread is still the same.
    if (!OnIconThread(base, state->tray) || !TrayHookImageBoundary(state->nonce)) return 2;
    HRESULT result = getIcon((void *)iface, (void **)&image);
    if (SUCCEEDED(result) && image &&
        SUCCEEDED(image->lpVtbl->QueryInterface(image, &WD_IID_SOFTWARE_BITMAP, (void **)&bitmap)) && bitmap &&
        SUCCEEDED(bitmap->lpVtbl->QueryInterface(bitmap, &WD_IID_AGILE, (void **)&agile)) && agile) {
        status = 6;
        TrayImageBody after = {0};
        if (BitmapGeometry(bitmap, &identity) && ProbeOwner(item, &after) && MatchesOwnerProbe(&identity, &after) &&
            Active(state->nonce) && TrayHookImageBoundary(state->nonce) &&
            mutation == TrayHookCurrentMutation() &&
            SameVtable(iface, base + tray_image_layout.itemInterfaceVtableRva)) {
            identity.nonce = state->nonce;
            identity.sequence = TrayHookNextSequence();
            *job = (ImageJob){bitmap, identity, state->epoch};
            bitmap = NULL;
            status = 0;
        }
    }
    if (agile) agile->lpVtbl->Release(agile);
    if (bitmap) bitmap->lpVtbl->Release(bitmap);
    if (image) image->lpVtbl->Release(image);
    return status;
}

static BOOL SameModel(uintptr_t base, uintptr_t root, uintptr_t model, uintptr_t manager) {
    uintptr_t now = 0, currentModel = 0, currentManager = 0;
    return ReadPointer(base + tray_image_layout.rootRva, &now) && now == root &&
        ReadPointer(root + tray_image_layout.rootModelOffset, &currentModel) && currentModel == model &&
        ReadPointer(currentModel + tray_image_layout.modelManagerOffset, &currentManager) && currentManager == manager;
}

static WdBufferFactory *CreateBufferFactory(void) {
    static const WCHAR name[] = L"Windows.Storage.Streams.Buffer";
    HSTRING text = NULL;
    WdBufferFactory *factory = NULL;
    if (SUCCEEDED(WindowsCreateString(name, (UINT32)(sizeof(name) / sizeof(WCHAR) - 1), &text))) {
        HRESULT result = RoGetActivationFactory(text, &WD_IID_BUFFER_FACTORY, (void **)&factory);
        WindowsDeleteString(text);
        if (FAILED(result)) factory = NULL;
    }
    return factory;
}

static TrayImageResult *CopyPixels(const ImageJob *job, WdBufferFactory *factory) {
    TrayImageBody geometry = {0};
    if (!BitmapGeometry(job->bitmap, &geometry) || geometry.width != job->body.width ||
        geometry.height != job->body.height || geometry.pixelBytes != job->body.pixelBytes) return NULL;
    WdBuffer *buffer = NULL;
    WdBufferByteAccess *access = NULL;
    TrayImageResult *result = NULL;
    UINT32 capacity = 0, length = 0;
    BYTE *source = NULL;
    if (FAILED(factory->lpVtbl->Create(factory, geometry.pixelBytes, &buffer)) || !buffer) goto done;
    if (FAILED(buffer->lpVtbl->get_Capacity(buffer, &capacity)) || capacity < geometry.pixelBytes ||
        FAILED(buffer->lpVtbl->put_Length(buffer, geometry.pixelBytes)) ||
        FAILED(job->bitmap->lpVtbl->CopyToBuffer(job->bitmap, buffer)) ||
        FAILED(buffer->lpVtbl->get_Length(buffer, &length)) || length != geometry.pixelBytes ||
        FAILED(buffer->lpVtbl->QueryInterface(buffer, &WD_IID_BUFFER_BYTES, (void **)&access)) || !access ||
        FAILED(access->lpVtbl->Buffer(access, &source)) || !source) goto done;
    result = HeapAlloc(GetProcessHeap(), 0, sizeof(*result) + geometry.pixelBytes);
    if (result) {
        result->body = job->body;
        // This address belongs to our owned Buffer in this process. It is never
        // a raw Explorer image pointer and is not exposed outside the packet.
        CopyMemory(result->pixels, source, geometry.pixelBytes);
    }
done:
    if (access) access->lpVtbl->Release(access);
    if (buffer) buffer->lpVtbl->Release(buffer);
    return result;
}

void TrayImagesFreeResult(TrayImageResult *result) {
    if (result) HeapFree(GetProcessHeap(), 0, result);
}

static DWORD WINAPI ImageWorker(void *parameter) {
    ImageEngine *state = parameter;
    HMODULE held = state->module;
    HRESULT initialized = RoInitialize(RO_INIT_MULTITHREADED);
    TrayImageProfile *profile = TrayImageProfileCreate();
    TrayProcessSequences *processSequences = TrayProcessSequencesCreate();
    WdBufferFactory *factory = SUCCEEDED(initialized) ? CreateBufferFactory() : NULL;
    AcquireSRWLockExclusive(&imageLock);
    state->profile = profile;
    state->processSequences = processSequences;
    state->profileRejected = profile == NULL;
    state->pixelsRejected = factory == NULL;
    state->ready = TRUE;
    ReleaseSRWLockExclusive(&imageLock);
    for (;;) {
        ImageJob job = {0};
        BOOL haveJob = FALSE, finish = FALSE;
        AcquireSRWLockExclusive(&imageLock);
        if (state->count) {
            job = state->jobs[0];
            if (--state->count) state->jobs[0] = state->jobs[1];
            state->inFlight = TRUE; haveJob = TRUE;
        } else if (!Active(state->nonce)) { state->ending = TRUE; finish = TRUE; }
        ReleaseSRWLockExclusive(&imageLock);
        if (finish) break;
        if (!haveJob) { WaitForSingleObject(state->wake, 250); continue; }
        DWORD status = 7;
        TrayImageResult *pixels = NULL;
        // Cancellation cannot abort a WinRT call. There is only one worker, so
        // a stalled call retains its module reference and cannot spawn copies.
        if (Active(job.body.nonce) && factory) {
            pixels = CopyPixels(&job, factory);
            status = pixels ? 0 : 6;
        }
        job.bitmap->lpVtbl->Release(job.bitmap);
        BOOL published = FALSE;
        if (pixels) {
            AcquireSRWLockExclusive(&imageLock);
            BOOL current = Active(job.body.nonce) && state->nonce == job.body.nonce && state->epoch == job.epoch;
            ReleaseSRWLockExclusive(&imageLock);
            if (current) { published = TrayHookPublishImage(pixels); if (!published) status = 8; }
            else status = 7;
            if (!published) TrayImagesFreeResult(pixels);
        }
        BOOL report = FALSE;
        DWORD nonce = 0, sampled = 0, sent = 0, roundStatus = status;
        AcquireSRWLockExclusive(&imageLock);
        state->inFlight = FALSE;
        if (state->epoch == job.epoch && state->nonce == job.body.nonce && Active(job.body.nonce)) {
            if (published) {
                ++state->published;
                InterlockedExchange(&statisticsPublished, (LONG)state->published);
            }
            if (status) state->roundStatus = status;
            if (!state->count) {
                report = TRUE; nonce = state->nonce; sampled = state->sampled;
                sent = state->published; roundStatus = state->roundStatus;
            }
        }
        ReleaseSRWLockExclusive(&imageLock);
        if (report) Report(nonce, roundStatus, sampled, sent);
    }
    // No private object was ever moved to this thread. All owned bitmaps and
    // factory references are gone before the engine allows a replacement worker.
    if (factory) factory->lpVtbl->Release(factory);
    TrayProcessSequencesDestroy(processSequences);
    if (profile) TrayImageProfileDestroy(profile);
    if (SUCCEEDED(initialized)) RoUninitialize();
    AcquireSRWLockExclusive(&imageLock);
    if (engine == state) engine = NULL;
    CloseHandle(state->wake);
    ReleaseSRWLockExclusive(&imageLock);
    HeapFree(GetProcessHeap(), 0, state);
    FreeLibraryAndExitThread(held, 0);
    return 0;
}

void TrayImagesBegin(HMODULE module, HWND tray, DWORD host, DWORD nonce) {
    if (!module || !tray || !host || !nonce) return;
    imageModule = module;
    if (!TryAcquireSRWLockExclusive(&imageLock)) return;
    if (engine && engine->ending) { ReleaseSRWLockExclusive(&imageLock); return; }
    if (engine) {
        if (engine->nonce != nonce || engine->host != host || engine->tray != tray || !Active(nonce)) {
            engine->host = host; engine->nonce = nonce; engine->tray = tray; ++engine->epoch;
            engine->sampled = engine->published = engine->roundStatus = engine->cursor = 0;
            engine->lastManager = 0;
            ResetStatistics(nonce);
            InterlockedExchange(&activeNonce, (LONG)nonce);
            SetEvent(engine->wake);
        }
        ReleaseSRWLockExclusive(&imageLock);
        return;
    }
    ImageEngine *state = HeapAlloc(GetProcessHeap(), HEAP_ZERO_MEMORY, sizeof(*state));
    HMODULE held = NULL;
    if (state && GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS,
        (LPCWSTR)(ULONG_PTR)&TrayImagesBegin, &held)) {
        state->module = held; state->tray = tray; state->host = host; state->nonce = nonce; state->epoch = 1;
        state->wake = CreateEventW(NULL, FALSE, FALSE, NULL);
        if (state->wake) {
            engine = state;
            ResetStatistics(nonce);
            InterlockedExchange(&activeNonce, (LONG)nonce);
            HANDLE worker = CreateThread(NULL, 0, ImageWorker, state, 0, NULL);
            if (worker) { CloseHandle(worker); ReleaseSRWLockExclusive(&imageLock); return; }
            engine = NULL;
            InterlockedCompareExchange(&activeNonce, 0, (LONG)nonce);
            CloseHandle(state->wake);
        }
    }
    if (held) FreeLibrary(held);
    if (state) HeapFree(GetProcessHeap(), 0, state);
    ReleaseSRWLockExclusive(&imageLock);
}

void TrayImagesCancel(DWORD nonce) {
    InterlockedCompareExchange(&activeNonce, 0, (LONG)nonce);
    if (TryAcquireSRWLockExclusive(&imageLock)) {
        if (engine && !engine->ending && engine->nonce == nonce) SetEvent(engine->wake);
        ReleaseSRWLockExclusive(&imageLock);
    }
}

void TrayImagesRequest(HWND tray, DWORD host, DWORD nonce) {
    if (!TrayHookImageBoundary(nonce)) { Report(nonce, 3, 0, 0); return; }
    if (!TryAcquireSRWLockExclusive(&imageLock)) { Report(nonce, 4, 0, 0); return; }
    ImageEngine *state = engine;
    if (!state || state->ending || state->host != host || state->nonce != nonce || state->tray != tray || !Active(nonce)) {
        ReleaseSRWLockExclusive(&imageLock);
        TrayImagesBegin(imageModule, tray, host, nonce);
        Report(nonce, 4, 0, 0);
        return;
    }
    DWORD status = 0, sampled = state->sampled, published = state->published;
    if (!state->ready || state->inFlight || state->count) status = 4;
    else if (state->profileRejected) status = 1;
    else if (state->pixelsRejected) status = 6;
    if (status) { ReleaseSRWLockExclusive(&imageLock); Report(nonce, status, sampled, published); return; }
    uintptr_t base = TrayImageProfileBase(state->profile);
    if (!OnIconThread(base, tray)) status = 2;
    else if (!TrayImageProfileValidateLive(state->profile)) { state->profileRejected = TRUE; status = 1; }
    if (status) { ReleaseSRWLockExclusive(&imageLock); Report(nonce, status, sampled, published); return; }
    uintptr_t root = 0, model = 0, manager = 0, begin = 0, end = 0, capacity = 0;
    IUnknown *rootReference = NULL;
    IUnknown *candidates[IMAGE_JOBS] = {0};
    uintptr_t candidateItems[IMAGE_JOBS] = {0};
    TrayImageBody candidateOwners[IMAGE_JOBS] = {0};
    unsigned int candidateCount = 0;
    uint64_t requestMutation = TrayHookCurrentMutation();
    if (ReadPointer(base + tray_image_layout.rootRva, &root) &&
        SameVtable(root, base + tray_image_layout.rootVtableRva)) {
        rootReference = (IUnknown *)root;
        rootReference->lpVtbl->AddRef(rootReference);
        if (!ReadPointer(root + tray_image_layout.rootModelOffset, &model) ||
            !ReadPointer(model + tray_image_layout.modelManagerOffset, &manager) ||
            !TrayImageReadMemory(manager + tray_image_layout.managerBeginOffset, &begin, sizeof(begin)) ||
            !TrayImageReadMemory(manager + tray_image_layout.managerEndOffset, &end, sizeof(end)) ||
            !TrayImageReadMemory(manager + tray_image_layout.managerCapacityOffset, &capacity, sizeof(capacity)) ||
            begin > end || end > capacity || (begin & 7) || ((end - begin) & 7) || ((capacity - begin) & 7) ||
            capacity - begin > MAX_MODEL_ICONS * sizeof(uintptr_t)) status = 5;
    } else status = 5;
    if (!status) {
        unsigned int available = (unsigned int)((end - begin) / sizeof(uintptr_t));
        if (manager != state->lastManager || state->cursor >= available) state->cursor = 0;
        state->lastManager = manager;
        unsigned int attempts = available < IMAGE_JOBS ? available : IMAGE_JOBS;
        DWORD lastFailure = 5;
        // Acquire every private item reference before the first COM getter.
        // These AddRef implementations are profile-verified atomic operations.
        // No manager/vector address is accessed after this acquisition phase.
        for (unsigned int i = 0; i < attempts && Active(nonce); ++i) {
            uintptr_t item = 0;
            unsigned int index = state->cursor++;
            if (state->cursor >= available) state->cursor = 0;
            if (ReadPointer(begin + (uintptr_t)index * sizeof(uintptr_t), &item) &&
                item <= UINTPTR_MAX - tray_image_layout.itemInterfaceOffset &&
                SameVtable(item, base + tray_image_layout.itemVtableRva) &&
                SameVtable(item + tray_image_layout.itemInterfaceOffset, base + tray_image_layout.itemInterfaceVtableRva)) {
                IUnknown *reference = (IUnknown *)(item + tray_image_layout.itemInterfaceOffset);
                reference->lpVtbl->AddRef(reference);
                candidateItems[candidateCount] = item;
                candidates[candidateCount++] = reference;
            }
        }
        DWORD deniedProcesses[IMAGE_JOBS] = {0};
        unsigned int deniedIndices[IMAGE_JOBS] = {0}, deniedCount = 0;
        uint64_t beforeSequences[IMAGE_JOBS] = {0};
        for (unsigned int i = 0; i < candidateCount; ++i) {
            if (!ProbeOwner(candidateItems[i], &candidateOwners[i])) {
                ZeroMemory(&candidateOwners[i], sizeof(candidateOwners[i]));
                continue;
            }
            if (candidateOwners[i].instanceKind == TRAY_INSTANCE_SYSTEM_SEQUENCE) {
                deniedProcesses[deniedCount] = candidateOwners[i].process;
                deniedIndices[deniedCount++] = i;
            }
        }
        // Two candidates share one fresh pre-getter snapshot. A failed or
        // oversized query only excludes denied-owner candidates from this round.
        if (deniedCount && TrayProcessSequencesRead(state->processSequences,
            deniedProcesses, beforeSequences, deniedCount)) {
            for (unsigned int i = 0; i < deniedCount; ++i)
                candidateOwners[deniedIndices[i]].instanceValue = beforeSequences[i];
        }
        for (unsigned int i = 0; i < candidateCount; ++i) {
            if (!Active(nonce) || !TrayHookImageBoundary(nonce) || requestMutation != TrayHookCurrentMutation() ||
                !SameModel(base, root, model, manager)) { status = 3; break; }
            if (!candidateOwners[i].instanceValue) { lastFailure = 5; continue; }
            ImageJob job = {0};
            DWORD failure = CaptureItem(state, candidateItems[i], &candidateOwners[i], &job);
            if (!failure) state->jobs[state->count++] = job;
            else lastFailure = failure;
        }
        // Re-query after all COM getters. No cached PID result can complete a
        // sequence identity. The image lock keeps these jobs private until this
        // check has removed every missing, recycled or changed owner.
        deniedCount = 0;
        for (unsigned int i = 0; i < state->count; ++i) {
            if (state->jobs[i].body.instanceKind == TRAY_INSTANCE_SYSTEM_SEQUENCE) {
                deniedProcesses[deniedCount] = state->jobs[i].body.process;
                deniedIndices[deniedCount++] = i;
            }
        }
        if (deniedCount) {
            uint64_t afterSequences[IMAGE_JOBS] = {0};
            BOOL verified = TrayProcessSequencesRead(state->processSequences,
                deniedProcesses, afterSequences, deniedCount);
            for (unsigned int i = 0; i < deniedCount; ++i) {
                ImageJob *job = &state->jobs[deniedIndices[i]];
                if (!verified || afterSequences[i] != job->body.instanceValue || !SameWindowOwner(&job->body)) {
                    job->bitmap->lpVtbl->Release(job->bitmap);
                    job->bitmap = NULL; lastFailure = 5;
                }
            }
            unsigned int kept = 0;
            for (unsigned int i = 0; i < state->count; ++i)
                if (state->jobs[i].bitmap) state->jobs[kept++] = state->jobs[i];
            state->count = kept;
        }
        if (!available) status = 0;
        else if (!state->count && !status) status = lastFailure;
    }
    for (unsigned int i = 0; i < candidateCount; ++i) candidates[i]->lpVtbl->Release(candidates[i]);
    if (rootReference && model && manager && (!Active(nonce) || !TrayHookImageBoundary(nonce) || requestMutation != TrayHookCurrentMutation() ||
        !SameModel(base, root, model, manager))) status = 3;
    if (rootReference) rootReference->lpVtbl->Release(rootReference);
    if (status == 3 || !Active(nonce) || requestMutation != TrayHookCurrentMutation()) {
        while (state->count) {
            WdSoftwareBitmap *bitmap = state->jobs[--state->count].bitmap;
            bitmap->lpVtbl->Release(bitmap);
        }
        if (!Active(nonce)) status = 7;
        else status = 3;
    }
    state->sampled += state->count;
    InterlockedExchange(&statisticsSampled, (LONG)state->sampled);
    sampled = state->sampled; published = state->published;
    BOOL pending = state->count != 0;
    if (pending) { state->roundStatus = status; SetEvent(state->wake); }
    ReleaseSRWLockExclusive(&imageLock);
    if (!pending) Report(nonce, status, sampled, published);
}
