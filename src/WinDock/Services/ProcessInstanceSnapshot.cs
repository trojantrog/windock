using System.Buffers.Binary;
using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace WinDock.Services;

internal enum ProcessInstanceKind { Unknown = 0, CreationTime = 1, SystemSequence = 2 }

internal readonly record struct ProcessInstanceKey(ProcessInstanceKind Kind, ulong Value)
{
    internal bool IsValid => Value != 0 && Kind is ProcessInstanceKind.CreationTime or ProcessInstanceKind.SystemSequence;
}

// SystemBasicProcessInformation is documented from Windows 11 26100.4770.
// Its sequence identifies a process instance without opening that process.
// SDK ABI: microsoft/win32metadata, commit 64ed9177fb76eb244fc9051c74b8a93fdfe20873,
// generation/WinSDK/RecompiledIdlHeaders/um/winternl.h (class 252, x64 entry 48).
// This is an observation, not a held process lifetime or permission to send
// owner callbacks. Call from a background thread, never from a UI predicate.
internal static class ProcessInstanceSnapshot
{
    internal sealed record Result(string Status, IReadOnlyDictionary<uint, ProcessInstanceKey> Instances)
    {
        internal bool Succeeded => Status is "available" or "partial" or "not_found" or "empty";
    }

    const int InformationClass = 252, EntrySize = 48, MaximumCandidates = 512;
    const int MaximumBytes = 8 * 1024 * 1024, MaximumEntries = 65536, MaximumAttempts = 4;
    const int LengthMismatch = unchecked((int)0xC0000004), BufferTooSmall = unchecked((int)0xC0000023);
    const int InvalidInformationClass = unchecked((int)0xC0000003), NotImplemented = unchecked((int)0xC0000002);
    static readonly SemaphoreSlim worker = new(1, 1);
    static readonly IReadOnlyDictionary<uint, ProcessInstanceKey> Empty =
        new ReadOnlyDictionary<uint, ProcessInstanceKey>(new Dictionary<uint, ProcessInstanceKey>());

    internal static Result Read(IReadOnlyCollection<uint> processIds, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(processIds);
        cancellation.ThrowIfCancellationRequested();
        if (!Environment.Is64BitProcess || RuntimeInformation.ProcessArchitecture != Architecture.X64)
            return Failure("unsupported_architecture");
        if (processIds.Count > MaximumCandidates) return Failure("candidate_limit");
        var requested = new HashSet<uint>();
        foreach (var pid in processIds)
        {
            if (pid == 0 || pid > int.MaxValue) return Failure("invalid_candidate");
            requested.Add(pid);
            if (requested.Count > MaximumCandidates) return Failure("candidate_limit");
        }
        if (requested.Count == 0) return Failure("empty");
        if (!worker.Wait(0)) return Failure("busy");
        var deadline = Environment.TickCount64 + 500;
        try
        {
            var capacity = 64 * 1024;
            for (var attempt = 0; attempt < MaximumAttempts; ++attempt)
            {
                cancellation.ThrowIfCancellationRequested();
                if (Environment.TickCount64 >= deadline) return Failure("deadline");
                var buffer = new byte[capacity];
                var pinned = GCHandle.Alloc(buffer, GCHandleType.Pinned);
                try
                {
                    var result = NtQuerySystemInformation(InformationClass, pinned.AddrOfPinnedObject(),
                        (uint)buffer.Length, out var returned);
                    // A native call cannot be forcibly cancelled. Its result is
                    // never used after the caller cancelled or the budget expired.
                    cancellation.ThrowIfCancellationRequested();
                    if (Environment.TickCount64 >= deadline) return Failure("deadline");
                    if (result == LengthMismatch || result == BufferTooSmall)
                    {
                        var next = Math.Max((long)capacity * 2, (long)returned + 4096);
                        next = (next + 4095) & ~4095L;
                        if (next > MaximumBytes) return Failure("buffer_limit");
                        capacity = (int)next;
                        continue;
                    }
                    if (result != 0)
                        return Failure(result is InvalidInformationClass or NotImplemented
                            ? "unsupported" : $"query_failed_{unchecked((uint)result):X8}");
                    if (returned < EntrySize || returned > buffer.Length) return Failure("invalid_buffer");
                    return Parse(buffer.AsSpan(0, (int)returned), requested, deadline, cancellation);
                }
                finally
                {
                    // The system buffer also contains names for unrelated
                    // processes. Do not decode, retain, or log them.
                    CryptographicOperations.ZeroMemory(buffer);
                    pinned.Free();
                }
            }
            return Failure("retry_limit");
        }
        catch (Exception error) when (error is EntryPointNotFoundException or DllNotFoundException
            or BadImageFormatException or NotSupportedException or System.Security.SecurityException
            or OutOfMemoryException)
        { return Failure("unavailable"); }
        finally { worker.Release(); }
    }

    static Result Parse(ReadOnlySpan<byte> buffer, HashSet<uint> requested, long deadline, CancellationToken cancellation)
    {
        var instances = new Dictionary<uint, ProcessInstanceKey>();
        var seen = new HashSet<uint>();
        var offset = 0;
        for (var count = 0; count < MaximumEntries; ++count)
        {
            cancellation.ThrowIfCancellationRequested();
            if (Environment.TickCount64 >= deadline) return Failure("deadline");
            if ((offset & 7) != 0 || buffer.Length - offset < EntrySize) return Failure("invalid_buffer");
            var entry = buffer.Slice(offset, EntrySize);
            var next = BinaryPrimitives.ReadUInt32LittleEndian(entry);
            var rawPid = BinaryPrimitives.ReadUInt64LittleEndian(entry[8..]);
            var sequence = BinaryPrimitives.ReadUInt64LittleEndian(entry[24..]);
            if (rawPid > uint.MaxValue || !seen.Add((uint)rawPid)) return Failure("invalid_buffer");
            var pid = (uint)rawPid;
            if (requested.Contains(pid))
            {
                if (sequence == 0) return Failure("invalid_instance");
                instances.Add(pid, new(ProcessInstanceKind.SystemSequence, sequence));
            }
            if (next == 0)
            {
                cancellation.ThrowIfCancellationRequested();
                if (Environment.TickCount64 >= deadline) return Failure("deadline");
                var status = instances.Count == requested.Count ? "available" : instances.Count == 0 ? "not_found" : "partial";
                return new(status, new ReadOnlyDictionary<uint, ProcessInstanceKey>(instances));
            }
            // Do not follow ImageName.Buffer, parent PIDs, or unrequested data.
            // Every next entry must leave room for its complete fixed header.
            if ((next & 7) != 0 || next < EntrySize || next > buffer.Length - offset - EntrySize)
                return Failure("invalid_buffer");
            offset += (int)next;
        }
        return Failure("entry_limit");
    }

    internal static bool IsCurrent(uint processId, ProcessInstanceKey instance, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        if (!instance.IsValid || processId == 0 || processId > int.MaxValue) return false;
        if (instance.Kind == ProcessInstanceKind.SystemSequence)
            return Read(new[] { processId }, cancellation).Instances.TryGetValue(processId, out var current) && current == instance;
        // The established creation-time path retains its strict process handle
        // and synchronization check; it never falls through to a sequence query.
        var handle = OpenProcess(0x101000, false, processId);
        if (handle == 0) return false;
        try
        {
            cancellation.ThrowIfCancellationRequested();
            var current = WaitForSingleObject(handle, 0) == 258
                && GetProcessTimes(handle, out var created, out _, out _, out _) && created == instance.Value
                && WaitForSingleObject(handle, 0) == 258;
            cancellation.ThrowIfCancellationRequested();
            return current;
        }
        finally { CloseHandle(handle); }
    }

    static Result Failure(string status) => new(status, Empty);

    [DllImport("ntdll.dll", ExactSpelling = true)]
    static extern int NtQuerySystemInformation(int informationClass, nint information, uint length, out uint returned);
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    static extern nint OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint processId);
    [DllImport("kernel32.dll", ExactSpelling = true)]
    static extern uint WaitForSingleObject(nint process, uint milliseconds);
    [DllImport("kernel32.dll", ExactSpelling = true)] [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool GetProcessTimes(nint process, out ulong created, out ulong exited, out ulong kernel, out ulong user);
    [DllImport("kernel32.dll", ExactSpelling = true)] [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool CloseHandle(nint process);
}
