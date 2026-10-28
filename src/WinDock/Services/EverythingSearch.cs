using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Interop;
using System.Windows.Threading;

namespace WinDock.Services;

public sealed record EverythingBatch(IReadOnlyList<SearchHit> Hits, int Total);

// Everything's local QUERY2W protocol; no HTTP server or SDK DLL is required.
// https://www.voidtools.com/support/everything/sdk/ipc/
public static class EverythingSearch
{
    const int CopyDataMessage = 0x4A, QueryUnicode = 18, FullPath = 4;
    [StructLayout(LayoutKind.Sequential)]
    struct CopyData { public nuint Id; public int Size; public nint Data; }
    [DllImport("user32.dll", SetLastError = true)]
    static extern nint SendMessageTimeout(nint window, uint message, nint sender, ref CopyData data, uint flags, uint timeout, out nint result);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool ChangeWindowMessageFilterEx(nint window, uint message, uint action, nint filter);

    public static Task<EverythingBatch> QueryAsync(string query, string? instance, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        var completion = new TaskCompletionSource<EverythingBatch>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() => Query(query, instance, cancellation, completion)) { IsBackground = true, Name = "Everything search" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    static void Query(string query, string? instance, CancellationToken cancellation, TaskCompletionSource<EverythingBatch> completion)
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        try
        {
            cancellation.ThrowIfCancellationRequested();
            var name = string.IsNullOrWhiteSpace(instance) ? "EVERYTHING_TASKBAR_NOTIFICATION" : "EVERYTHING_TASKBAR_NOTIFICATION_(" + instance.Trim() + ")";
            var target = Native.FindWindow(name, null);
            if (target == 0) throw new InvalidOperationException(Loc.T("everythingUnavailable"));
            Native.GetWindowThreadProcessId(target, out var targetProcess);
            if (targetProcess == 0) throw new InvalidOperationException(Loc.T("everythingUnavailable"));
            using var receiver = new HwndSource(new HwndSourceParameters("WinDock Everything reply")
            { ParentWindow = new nint(-3), WindowStyle = 0, Width = 0, Height = 0 });
            if (!ChangeWindowMessageFilterEx(receiver.Handle, CopyDataMessage, 1, 0))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            var replyId = (uint)Random.Shared.Next(1, int.MaxValue);
            receiver.AddHook((nint window, int message, nint sender, nint data, ref bool handled) =>
            {
                if (message != CopyDataMessage || data == 0) return 0;
                var reply = Marshal.PtrToStructure<CopyData>(data);
                if (reply.Id != replyId) return 0;
                Native.GetWindowThreadProcessId(sender, out var senderProcess);
                if (senderProcess != targetProcess) return 0;
                handled = true;
                try
                {
                    if (reply.Size is < 20 or > 8_000_000 || reply.Data == 0) throw new InvalidDataException();
                    var bytes = new byte[reply.Size]; Marshal.Copy(reply.Data, bytes, 0, bytes.Length);
                    completion.TrySetResult(ParseResponse(bytes));
                }
                catch (Exception e) when (e is InvalidDataException or ArgumentException or OverflowException) { completion.TrySetException(new InvalidDataException(Loc.T("everythingInvalidReply"), e)); }
                dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
                return 1;
            });
            var payload = new byte[28 + Encoding.Unicode.GetByteCount(query) + 2];
            BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(0), unchecked((uint)(long)receiver.Handle));
            BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(4), replyId);
            BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(16), 100);
            BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(20), FullPath);
            BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(24), 1);
            Encoding.Unicode.GetBytes(query, payload.AsSpan(28));
            var allocated = Marshal.AllocHGlobal(payload.Length);
            try
            {
                Marshal.Copy(payload, 0, allocated, payload.Length);
                var request = new CopyData { Id = QueryUnicode, Size = payload.Length, Data = allocated };
                if (SendMessageTimeout(target, CopyDataMessage, receiver.Handle, ref request, 2, 1500, out var accepted) == 0 || accepted == 0)
                    throw new InvalidOperationException(Loc.T("everythingUnavailable"));
            }
            finally { Marshal.FreeHGlobal(allocated); }
            using var registration = cancellation.Register(() =>
            {
                completion.TrySetCanceled(cancellation);
                if (!dispatcher.HasShutdownStarted) dispatcher.BeginInvokeShutdown(DispatcherPriority.Send);
            });
            var timer = new DispatcherTimer(TimeSpan.FromSeconds(4), DispatcherPriority.Normal, (_, _) =>
            {
                completion.TrySetException(new TimeoutException(Loc.T("everythingTimeout")));
                dispatcher.BeginInvokeShutdown(DispatcherPriority.Send);
            }, dispatcher);
            if (!completion.Task.IsCompleted) Dispatcher.Run();
            timer.Stop();
        }
        catch (OperationCanceledException) { completion.TrySetCanceled(cancellation); }
        catch (Exception e) { completion.TrySetException(e); }
        finally { if (!dispatcher.HasShutdownStarted) dispatcher.InvokeShutdown(); }
    }

    public static EverythingBatch ParseResponse(ReadOnlySpan<byte> data)
    {
        if (data.Length < 20) throw new InvalidDataException();
        var total = BinaryPrimitives.ReadInt32LittleEndian(data);
        var count = BinaryPrimitives.ReadInt32LittleEndian(data[4..]);
        if (count is < 0 or > 100 || total < count || BinaryPrimitives.ReadInt32LittleEndian(data[12..]) != FullPath || data.Length < 20 + count * 8)
            throw new InvalidDataException();
        var hits = new List<SearchHit>();
        for (var i = 0; i < count; i++)
        {
            var item = data[(20 + i * 8)..];
            var flags = BinaryPrimitives.ReadUInt32LittleEndian(item);
            var offset = BinaryPrimitives.ReadInt32LittleEndian(item[4..]);
            if (offset < 20 + count * 8 || offset > data.Length - 4) throw new InvalidDataException();
            var length = BinaryPrimitives.ReadInt32LittleEndian(data[offset..]);
            if (length is < 1 or > 32767 || data.Length - offset - 4 < (length + 1) * 2) throw new InvalidDataException();
            var text = data.Slice(offset + 4, length * 2);
            if (BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(offset + 4 + length * 2)) != 0) throw new InvalidDataException();
            var path = Encoding.Unicode.GetString(text);
            if (path.Contains('\0') || !Path.IsPathFullyQualified(path)) throw new InvalidDataException();
            var name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar));
            hits.Add(new SearchHit(name.Length == 0 ? path : name, path, (flags & 3) != 0 ? "folder" : "file", path));
        }
        return new EverythingBatch(hits, total);
    }
}
