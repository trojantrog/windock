using System.Buffers.Binary;
using System.ComponentModel;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows.Media.Imaging;
using Microsoft.Win32.SafeHandles;

namespace WinDock.Services;

// This reader never executes Explorer code. Its layout is supplied only by
// embedded profiles derived from a particular Microsoft image and matching PDB.
// A Windows update, patched method, inaccessible page or changing registration
// rejects the read; it must not turn an unverified offset into a callback.
internal static class NativeTrayModelReader
{
    internal sealed record Record(nint Window, uint ProcessId, uint ThreadId, uint Id, Guid Guid,
        uint Callback, uint Version, long ProcessStarted, string ProcessPath, string OwnerClass,
        string? Tip, BitmapSource? Icon, bool? Hidden);

    static readonly SemaphoreSlim worker = new(1, 1);
    static volatile string status = "not_started";
    static int candidateCount, validatedCount;
    internal static string Status => status;
    internal static int CandidateCount => Volatile.Read(ref candidateCount);
    internal static int ValidatedCount => Volatile.Read(ref validatedCount);
    const int MaximumItems = 512;
    const ulong LastUserAddress = 0x0000800000000000;

    internal static async Task<IReadOnlyList<Record>> ReadAsync(nint explorerWindow, uint explorerProcess,
        CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if (!worker.Wait(0)) return [];
        var completion = new TaskCompletionSource<IReadOnlyList<Record>>(TaskCreationOptions.RunContinuationsAsynchronously);
        Interlocked.Exchange(ref candidateCount, 0);
        Interlocked.Exchange(ref validatedCount, 0);
        try
        {
            var thread = new Thread(() =>
            {
                try { completion.TrySetResult(Read(explorerWindow, explorerProcess, cancellation)); }
                catch (OperationCanceledException) { status = "cancelled"; completion.TrySetCanceled(cancellation); }
                catch (ReadRejected e) { status = e.Reason; completion.TrySetResult([]); }
                catch (Exception e) when (e is IOException or Win32Exception or UnauthorizedAccessException
                    or InvalidOperationException or ArgumentException or NotSupportedException or JsonException
                    or BadImageFormatException or System.Security.SecurityException or OverflowException or FormatException)
                { status = "unavailable:" + e.GetType().Name; completion.TrySetResult([]); }
                finally { worker.Release(); }
            }) { IsBackground = true, Name = "WinDock current tray inventory" };
            thread.SetApartmentState(ApartmentState.MTA);
            thread.Start();
        }
        catch { worker.Release(); throw; }
        // Cancellation stops the caller immediately. The actual worker retains
        // its reservation and every process handle until its read has ended.
        return await completion.Task.WaitAsync(cancellation).ConfigureAwait(false);
    }

    static IReadOnlyList<Record> Read(nint shellWindow, uint shellProcess, CancellationToken cancellation)
    {
        if (!Environment.Is64BitProcess || RuntimeInformation.ProcessArchitecture != Architecture.X64)
            throw new ReadRejected("architecture_unsupported");
        status = "reading";
        using var remote = new ReadSession(shellProcess, cancellation);
        var shellThread = Native.GetWindowThreadProcessId(shellWindow, out var shellPid);
        if (shellThread == 0 || shellPid != shellProcess || Native.ClassOf(shellWindow) != "Shell_TrayWnd"
            || !PathEquals(remote.Path, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe")))
            throw new ReadRejected("explorer_changed");

        var module = FindModule(shellProcess);
        var imagePath = Path.Combine(Environment.SystemDirectory, "Taskbar.dll");
        if (!PathEquals(module.Path, imagePath)) throw new ReadRejected("image_path_mismatch");
        using var image = new ImageEvidence(imagePath);
        var profile = FindProfile(image);
        image.ValidateProfile(profile);
        if (module.Size != profile.Image.SizeOfImage) throw new ReadRejected("image_mismatch");
        image.ValidateLoaded(remote, module.Address, profile);

        var layout = profile.Layout;
        var root = remote.Pointer(module.Address + profile.Symbols["root"].Rva);
        var model = remote.Pointer(Add(root, layout.Model));
        // The profile identifies the pointer member of the manager's shared_ptr,
        // not a remotely invoked accessor or an assumed C++/WinRT interface.
        var manager = remote.Pointer(Add(model, layout.Manager));
        var begin = remote.Value64(Add(manager, layout.ManagerVectorBegin));
        var end = remote.Value64(Add(manager, layout.ManagerVectorEnd));
        if (end < begin || (end - begin) % 8 != 0 || end - begin > MaximumItems * 8
            || (end != begin && !ValidPointer(begin))) throw new ReadRejected("collection_invalid");
        var entries = end == begin ? [] : remote.Read(begin, checked((int)(end - begin)));
        Interlocked.Exchange(ref candidateCount, entries.Length / 8);

        var results = new List<Record>();
        var owners = new Dictionary<uint, Owner>();
        var windows = new List<(nint Window, uint Process, uint Thread)>();
        var identities = new HashSet<string>(StringComparer.Ordinal);
        var objects = new HashSet<ulong>();
        try
        {
            for (var at = 0; at < entries.Length; at += 8)
            {
                remote.Check();
                var address = BinaryPrimitives.ReadUInt64LittleEndian(entries.AsSpan(at, 8));
                if (!ValidPointer(address) || !objects.Add(address)) throw new ReadRejected("collection_invalid");
                if (remote.Value64(address) != module.Address + profile.Symbols["itemVtable"].Rva)
                    throw new ReadRejected("item_type_mismatch");
                var window = (nint)remote.Value64(Add(address, layout.Window));
                var id = remote.Value32(Add(address, layout.Id));
                var guid = new Guid(remote.Read(Add(address, layout.Guid), 16));
                var callback = remote.Value32(Add(address, layout.Callback));
                var version = remote.Value32(Add(address, layout.Version));
                // A zero callback remains display-only. Unsupported values are
                // never interpreted as a different, guessed callback protocol.
                if ((callback != 0 && callback is < 0x400 or > 0xFFFF) || version is not (0 or 3 or 4)) continue;
                var thread = Native.GetWindowThreadProcessId(window, out var pid);
                if (thread == 0 || pid <= 0 || !Native.IsWindow(window)) continue;
                if (!owners.TryGetValue((uint)pid, out var owner))
                {
                    owner = Owner.TryOpen((uint)pid);
                    if (owner is null) continue;
                    owners.Add((uint)pid, owner);
                }
                if (!owner.Alive) throw new ReadRejected("owner_changed");
                var windowClass = Native.ClassOf(window);
                if (windowClass.Length == 0 || Native.GetWindowThreadProcessId(window, out var afterPid) != thread || afterPid != pid)
                    throw new ReadRejected("owner_changed");
                var key = guid == Guid.Empty ? $"{window:X}:{id}:{pid}" : guid.ToString("D");
                if (!identities.Add(key)) throw new ReadRejected("identity_ambiguous");
                // This container is the registration set, including both tray
                // placements. Placement does not establish NIS_HIDDEN. The local
                // image's icon field is a COM image object, not an HICON; neither
                // it nor an unverified HSTRING layout is dereferenced here.
                results.Add(new(window, (uint)pid, thread, id, guid, callback, version, owner.Started,
                    owner.Path, windowClass, null, null, null));
                windows.Add((window, (uint)pid, thread));
            }
            // Recheck every observed pointer, vector and protocol field as one
            // batch. The service additionally rejects a changed hook generation
            // or protocol sequence before it merges these records.
            remote.VerifyReads();
            foreach (var owner in owners.Values)
                if (!owner.Alive) throw new ReadRejected("owner_changed");
            foreach (var window in windows)
                if (Native.GetWindowThreadProcessId(window.Window, out var pid) != window.Thread || pid != window.Process)
                    throw new ReadRejected("owner_changed");
            if (Native.GetWindowThreadProcessId(shellWindow, out var currentPid) != shellThread || currentPid != shellProcess)
                throw new ReadRejected("explorer_changed");
            remote.Check();
            Interlocked.Exchange(ref validatedCount, results.Count);
            status = results.Count != 0 ? "available" : entries.Length == 0 ? "empty" : "filtered";
            return results.AsReadOnly();
        }
        finally { foreach (var owner in owners.Values) owner.Dispose(); }
    }

    sealed class ReadRejected(string reason) : Exception { internal string Reason => reason; }
    static bool PathEquals(string a, string b) => string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
    static bool ValidPointer(ulong address) => address >= 0x10000 && address < LastUserAddress && address % 8 == 0;
    static ulong Add(ulong address, int offset)
    {
        if (!ValidPointer(address) || offset is < 0 or > 4096 || address > LastUserAddress - (uint)offset)
            throw new ReadRejected("address_invalid");
        return address + (uint)offset;
    }

    sealed class Owner : IDisposable
    {
        internal nint Handle { get; }
        internal long Started { get; }
        internal string Path { get; }
        internal bool Alive => WaitForSingleObject(Handle, 0) == 258;
        Owner(nint handle, long started, string path) { Handle = handle; Started = started; Path = path; }
        internal static Owner? TryOpen(uint pid)
        {
            if (pid == 0 || pid > int.MaxValue) return null;
            var handle = Native.OpenProcess(0x101000, false, (int)pid);
            if (handle == 0) return null;
            try
            {
                var path = ProcessPath(handle);
                if (WaitForSingleObject(handle, 0) != 258 || path is null
                    || !GetProcessTimes(handle, out var started, out _, out _, out _) || started == 0) return null;
                var result = new Owner(handle, started, path); handle = 0; return result;
            }
            finally { if (handle != 0) Native.CloseHandle(handle); }
        }
        public void Dispose() => Native.CloseHandle(Handle);
    }

    sealed class ReadSession : IDisposable
    {
        readonly nint process;
        readonly CancellationToken cancellation;
        readonly long deadline = Environment.TickCount64 + 4000;
        readonly Dictionary<(ulong Address, int Size), byte[]> observations = [];
        int bytesRead, operations;
        internal string Path { get; }
        internal ReadSession(uint pid, CancellationToken token)
        {
            cancellation = token;
            if (pid == 0 || pid > int.MaxValue) throw new ReadRejected("explorer_unavailable");
            // Read/query/synchronize only. No write, VM operation or thread rights.
            process = Native.OpenProcess(0x100410, false, (int)pid);
            if (process == 0) throw new ReadRejected("process_access_unavailable");
            try
            {
                Path = ProcessPath(process) ?? throw new ReadRejected("explorer_unavailable");
                Check();
            }
            catch { Native.CloseHandle(process); throw; }
        }
        internal void Check()
        {
            cancellation.ThrowIfCancellationRequested();
            if (Environment.TickCount64 >= deadline) throw new ReadRejected("deadline");
            if (WaitForSingleObject(process, 0) != 258) throw new ReadRejected("explorer_changed");
        }
        internal ulong Pointer(ulong address)
        {
            var value = Value64(address);
            return ValidPointer(value) ? value : throw new ReadRejected("pointer_invalid");
        }
        internal ulong Value64(ulong address) => BinaryPrimitives.ReadUInt64LittleEndian(Read(address, 8));
        internal uint Value32(ulong address) => BinaryPrimitives.ReadUInt32LittleEndian(Read(address, 4));
        internal byte[] Read(ulong address, int size, bool remember = true, ulong imageBase = 0)
        {
            Check();
            if (size is <= 0 or > 65536 || address < 0x10000 || address >= LastUserAddress
                || (ulong)size > LastUserAddress - address || ++operations > 20000 || (bytesRead += size) > 4 * 1024 * 1024)
                throw new ReadRejected("read_limit");
            var limit = address + (uint)size;
            for (var cursor = address; cursor < limit;)
            {
                if (VirtualQueryEx(process, (nint)cursor, out var page, (nuint)Marshal.SizeOf<MemoryInformation>()) == 0
                    || page.State != 0x1000 || (page.Protect & 0x101) != 0
                    || (page.Protect & 0xFF) is not (2 or 4 or 8 or 0x20 or 0x40 or 0x80)
                    || (imageBase != 0 && (page.Type != 0x1000000 || (ulong)page.AllocationBase != imageBase)))
                    throw new ReadRejected("memory_unavailable");
                var next = checked((ulong)page.BaseAddress + (ulong)page.RegionSize);
                if ((ulong)page.BaseAddress > cursor || next <= cursor) throw new ReadRejected("memory_unavailable");
                cursor = next;
            }
            var buffer = new byte[size];
            if (!ReadProcessMemory(process, (nint)address, buffer, (nuint)size, out var copied) || copied != (nuint)size)
                throw new ReadRejected("memory_unavailable");
            if (remember)
            {
                var key = (address, size);
                if (observations.TryGetValue(key, out var earlier) && !buffer.AsSpan().SequenceEqual(earlier))
                    throw new ReadRejected("model_changed");
                observations[key] = buffer;
            }
            return buffer;
        }
        internal void VerifyReads()
        {
            foreach (var (key, value) in observations.Reverse())
                if (!Read(key.Address, key.Size, false).AsSpan().SequenceEqual(value)) throw new ReadRejected("model_changed");
        }
        internal void VerifyMappedFile(ulong address, SafeFileHandle file)
        {
            Check();
            var mapped = new StringBuilder(32768);
            var disk = new StringBuilder(32768);
            var mappedLength = K32GetMappedFileNameW(process, (nint)address, mapped, (uint)mapped.Capacity);
            // VOLUME_NAME_NT produces the same device-path form as PSAPI.
            var diskLength = GetFinalPathNameByHandleW(file, disk, (uint)disk.Capacity, 2);
            if (mappedLength == 0 || mappedLength >= mapped.Capacity || diskLength == 0 || diskLength >= disk.Capacity
                || !string.Equals(mapped.ToString(), disk.ToString(), StringComparison.OrdinalIgnoreCase))
                throw new ReadRejected("mapped_path_mismatch");
        }
        public void Dispose() => Native.CloseHandle(process);
    }

    sealed record Profile(int Schema, ImageIdentity Image, Dictionary<string, Symbol> Symbols, Layout Layout);
    sealed record ImageIdentity(string FileName, uint Machine, uint TimeDateStamp, uint SizeOfImage, string Sha256,
        string PdbName, Guid PdbGuid, uint PdbAge);
    sealed record Symbol(uint Rva, int Size, string? Sha256);
    sealed record Layout(int Model, int Manager, int ManagerVectorBegin, int ManagerVectorEnd,
        int Window, int Id, int Guid, int Callback, int Version);

    static Profile FindProfile(ImageEvidence image)
    {
        var assembly = typeof(NativeTrayModelReader).Assembly;
        foreach (var name in assembly.GetManifestResourceNames().Where(n => n.StartsWith("NativeTrayProfiles.", StringComparison.Ordinal)).Take(16))
        {
            using var resource = assembly.GetManifestResourceStream(name);
            if (resource is null || resource.Length > 512 * 1024) continue;
            var profile = JsonSerializer.Deserialize<Profile>(resource,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true, MaxDepth = 16 });
            if (profile?.Image is null || profile.Schema != 1
                || !string.Equals(profile.Image.Sha256, image.Hash, StringComparison.OrdinalIgnoreCase)) continue;
            return profile;
        }
        throw new ReadRejected("profile_unavailable");
    }

    // Disk and mapped-image checks are separate: the disk hash alone cannot
    // establish that Explorer is still using the same image after an update.
    sealed class ImageEvidence : IDisposable
    {
        readonly FileStream file;
        readonly PEReader pe;
        internal string Hash { get; }
        internal ImageEvidence(string path)
        {
            file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            try
            {
                if (file.Length is < 4096 or > 32 * 1024 * 1024) throw new ReadRejected("image_invalid");
                Hash = Convert.ToHexString(SHA256.HashData(file));
                file.Position = 0;
                pe = new PEReader(file, PEStreamOptions.LeaveOpen | PEStreamOptions.PrefetchEntireImage);
            }
            catch { file.Dispose(); throw; }
        }
        internal void ValidateProfile(Profile profile)
        {
            var target = profile.Image;
            var header = pe.PEHeaders.PEHeader ?? throw new ReadRejected("image_invalid");
            if (target.FileName != "Taskbar.dll" || target.Machine != 0x8664
                || (uint)pe.PEHeaders.CoffHeader.Machine != target.Machine || header.Magic != PEMagic.PE32Plus
                || (uint)pe.PEHeaders.CoffHeader.TimeDateStamp != target.TimeDateStamp || header.SizeOfImage != target.SizeOfImage
                || profile.Symbols is null || profile.Symbols.Count is < 7 or > 48 || profile.Layout is null)
                throw new ReadRejected("profile_invalid");
            var codeView = pe.ReadDebugDirectory().Where(e => e.Type == DebugDirectoryEntryType.CodeView).ToArray();
            if (codeView.Length != 1) throw new ReadRejected("image_pdb_mismatch");
            var pdb = pe.ReadCodeViewDebugDirectoryData(codeView[0]);
            if (pdb.Guid != target.PdbGuid || pdb.Age != target.PdbAge
                || !string.Equals(System.IO.Path.GetFileName(pdb.Path), target.PdbName, StringComparison.OrdinalIgnoreCase))
                throw new ReadRejected("image_pdb_mismatch");
            foreach (var name in new[] { "root", "itemVtable", "copyData", "notify", "getRect", "identity", "sendOwner" })
                if (!profile.Symbols.ContainsKey(name)) throw new ReadRejected("profile_invalid");
            foreach (var (name, symbol) in profile.Symbols)
            {
                if (symbol is null || symbol.Rva == 0 || symbol.Size is < 8 or > 65536
                    || !InImage(symbol.Rva, symbol.Size, target.SizeOfImage)) throw new ReadRejected("profile_invalid");
                var section = Section(symbol.Rva, symbol.Size);
                var executable = (section.SectionCharacteristics & SectionCharacteristics.MemExecute) != 0;
                if (name is "root" or "itemVtable")
                {
                    if (executable || (name == "itemVtable" && (section.SectionCharacteristics & SectionCharacteristics.MemWrite) != 0))
                        throw new ReadRejected("profile_invalid");
                    continue;
                }
                if (!executable || symbol.Sha256 is null) throw new ReadRejected("profile_invalid");
                var actual = pe.GetSectionData((int)symbol.Rva).GetContent(0, symbol.Size).ToArray();
                if (!string.Equals(Convert.ToHexString(SHA256.HashData(actual)), symbol.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new ReadRejected("profile_invalid");
                RejectCodeRelocations(symbol);
            }
            var l = profile.Layout;
            var offsets = new[] { l.Model, l.Manager, l.ManagerVectorBegin, l.ManagerVectorEnd, l.Window, l.Id, l.Guid, l.Callback, l.Version };
            if (offsets.Any(x => x is < 0 or > 4096) || l.Model == 0 || l.Manager == 0 || l.Window == 0
                || l.Model % 8 != 0 || l.Manager % 8 != 0 || l.Window % 8 != 0
                || l.ManagerVectorBegin % 8 != 0 || l.ManagerVectorEnd != l.ManagerVectorBegin + 8
                || l.Id != l.Window + 8 || l.Guid != l.Id + 4 || l.Callback % 4 != 0 || l.Version % 4 != 0
                || l.Callback == l.Version) throw new ReadRejected("profile_invalid");
        }
        SectionHeader Section(uint rva, int size)
        {
            foreach (var s in pe.PEHeaders.SectionHeaders)
                if (rva >= s.VirtualAddress && (ulong)rva + (uint)size <= (ulong)s.VirtualAddress + (uint)s.VirtualSize) return s;
            throw new ReadRejected("profile_invalid");
        }
        static bool InImage(uint rva, int length, uint size) => length > 0 && rva < size && (uint)length <= size - rva;
        void RejectCodeRelocations(Symbol function)
        {
            var directory = pe.PEHeaders.PEHeader!.BaseRelocationTableDirectory;
            if (directory.Size == 0) return;
            if (directory.Size is < 8 or > 1024 * 1024) throw new ReadRejected("image_invalid");
            var data = pe.GetSectionData(directory.RelativeVirtualAddress).GetContent(0, directory.Size).ToArray();
            for (var offset = 0; offset < data.Length;)
            {
                if (offset > data.Length - 8) throw new ReadRejected("image_invalid");
                var page = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset, 4));
                var block = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset + 4, 4));
                if (block < 8 || block % 2 != 0 || block > data.Length - offset) throw new ReadRejected("image_invalid");
                for (var cursor = offset + 8; cursor < offset + block; cursor += 2)
                {
                    var relocation = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(cursor, 2));
                    var address = (ulong)page + (uint)(relocation & 0xFFF);
                    if (relocation >> 12 != 0 && address < function.Rva + (ulong)function.Size && address + 8 > function.Rva)
                        throw new ReadRejected("relocated_evidence_unsupported");
                }
                offset += (int)block;
            }
        }
        internal void ValidateLoaded(ReadSession remote, ulong address, Profile profile)
        {
            var target = profile.Image;
            remote.VerifyMappedFile(address, file.SafeFileHandle);
            var dos = remote.Read(address, 64, imageBase: address);
            var ntOffset = BinaryPrimitives.ReadInt32LittleEndian(dos.AsSpan(0x3C, 4));
            if (dos[0] != 'M' || dos[1] != 'Z' || ntOffset is < 64 or > 4096) throw new ReadRejected("loaded_image_mismatch");
            var nt = remote.Read(address + (uint)ntOffset, 264, imageBase: address);
            if (BinaryPrimitives.ReadUInt32LittleEndian(nt) != 0x4550
                || BinaryPrimitives.ReadUInt16LittleEndian(nt.AsSpan(4)) != target.Machine
                || BinaryPrimitives.ReadUInt32LittleEndian(nt.AsSpan(8)) != target.TimeDateStamp
                || BinaryPrimitives.ReadUInt16LittleEndian(nt.AsSpan(24)) != 0x20B
                || BinaryPrimitives.ReadUInt32LittleEndian(nt.AsSpan(24 + 56)) != target.SizeOfImage
                || BinaryPrimitives.ReadUInt32LittleEndian(nt.AsSpan(24 + 108)) < 7)
                throw new ReadRejected("loaded_image_mismatch");
            var debugRva = BinaryPrimitives.ReadUInt32LittleEndian(nt.AsSpan(24 + 112 + 6 * 8));
            var debugSize = BinaryPrimitives.ReadUInt32LittleEndian(nt.AsSpan(24 + 116 + 6 * 8));
            if (debugSize is < 28 or > 4096 || debugSize % 28 != 0 || !InImage(debugRva, (int)debugSize, target.SizeOfImage))
                throw new ReadRejected("loaded_image_mismatch");
            var debug = remote.Read(address + debugRva, (int)debugSize, imageBase: address);
            var found = false;
            for (var i = 0; i < debug.Length; i += 28)
            {
                if (BinaryPrimitives.ReadUInt32LittleEndian(debug.AsSpan(i + 12)) != 2) continue;
                var size = BinaryPrimitives.ReadUInt32LittleEndian(debug.AsSpan(i + 16));
                var rva = BinaryPrimitives.ReadUInt32LittleEndian(debug.AsSpan(i + 20));
                if (found || size is < 25 or > 1024 || !InImage(rva, (int)size, target.SizeOfImage)) throw new ReadRejected("loaded_pdb_mismatch");
                var value = remote.Read(address + rva, (int)size, imageBase: address);
                var zero = Array.IndexOf(value, (byte)0, 24);
                if (BinaryPrimitives.ReadUInt32LittleEndian(value) != 0x53445352 || new Guid(value.AsSpan(4, 16)) != target.PdbGuid
                    || BinaryPrimitives.ReadUInt32LittleEndian(value.AsSpan(20)) != target.PdbAge || zero < 24
                    || !string.Equals(System.IO.Path.GetFileName(Encoding.UTF8.GetString(value, 24, zero - 24)), target.PdbName, StringComparison.OrdinalIgnoreCase))
                    throw new ReadRejected("loaded_pdb_mismatch");
                found = true;
            }
            if (!found) throw new ReadRejected("loaded_pdb_mismatch");
            foreach (var (name, symbol) in profile.Symbols)
            {
                var count = name is "root" or "itemVtable" ? 8 : symbol.Size;
                var bytes = remote.Read(address + symbol.Rva, count, imageBase: address);
                if (name == "root") continue;
                if (name == "itemVtable")
                {
                    // Vtable contents contain relocated addresses. Verify the
                    // first entry against this same file after ASLR adjustment.
                    var disk = pe.GetSectionData((int)symbol.Rva).GetContent(0, 8).ToArray();
                    var preferred = pe.PEHeaders.PEHeader!.ImageBase;
                    var pointer = BinaryPrimitives.ReadUInt64LittleEndian(disk);
                    if (pointer < preferred || pointer - preferred >= target.SizeOfImage
                        || (Section((uint)(pointer - preferred), 1).SectionCharacteristics & SectionCharacteristics.MemExecute) == 0
                        || BinaryPrimitives.ReadUInt64LittleEndian(bytes) != address + pointer - preferred)
                        throw new ReadRejected("loaded_vtable_mismatch");
                    continue;
                }
                if (!string.Equals(Convert.ToHexString(SHA256.HashData(bytes)), symbol.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new ReadRejected("loaded_code_mismatch");
            }
        }
        public void Dispose() { pe.Dispose(); file.Dispose(); }
    }

    static (ulong Address, uint Size, string Path) FindModule(uint pid)
    {
        var snapshot = CreateToolhelp32Snapshot(8, pid);
        if (snapshot == -1) throw new ReadRejected("module_unavailable");
        try
        {
            var module = new ModuleEntry { Size = (uint)Marshal.SizeOf<ModuleEntry>() };
            if (!Module32FirstW(snapshot, ref module)) throw new ReadRejected("module_unavailable");
            for (var count = 0; count < 2048; count++)
            {
                if (string.Equals(module.Module, "Taskbar.dll", StringComparison.OrdinalIgnoreCase))
                    return ((ulong)module.BaseAddress, module.ImageSize, module.Executable);
                if (!Module32NextW(snapshot, ref module)) break;
            }
            throw new ReadRejected("module_unavailable");
        }
        finally { Native.CloseHandle(snapshot); }
    }
    static string? ProcessPath(nint handle)
    {
        var text = new StringBuilder(32768); var length = text.Capacity;
        return Native.QueryFullProcessImageName(handle, 0, text, ref length) && length > 0 ? text.ToString() : null;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct ModuleEntry
    {
        public uint Size, ModuleId, ProcessId, GlobalUseCount, ProcessUseCount;
        public nint BaseAddress;
        public uint ImageSize;
        public nint ModuleHandle;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Module;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string Executable;
    }
    [StructLayout(LayoutKind.Sequential)]
    struct MemoryInformation
    {
        public nint BaseAddress, AllocationBase;
        public uint AllocationProtect;
        public ushort PartitionId;
        public nuint RegionSize;
        public uint State, Protect, Type;
    }
    [DllImport("kernel32.dll", SetLastError = true)] static extern nint CreateToolhelp32Snapshot(uint flags, uint process);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool Module32FirstW(nint snapshot, ref ModuleEntry entry);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool Module32NextW(nint snapshot, ref ModuleEntry entry);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool ReadProcessMemory(nint process, nint address, [Out] byte[] bytes, nuint count, out nuint copied);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern nuint VirtualQueryEx(nint process, nint address, out MemoryInformation information, nuint size);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern uint K32GetMappedFileNameW(nint process, nint address, StringBuilder text, uint size);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern uint GetFinalPathNameByHandleW(SafeFileHandle file, StringBuilder text, uint size, uint flags);
    [DllImport("kernel32.dll")] static extern uint WaitForSingleObject(nint handle, uint milliseconds);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool GetProcessTimes(nint process, out long created, out long exited, out long kernel, out long user);
}
