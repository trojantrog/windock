using System.Runtime.InteropServices;

namespace WinDock.Services;

internal static class TokenPulseNative
{
    const string Library = "winsqlite3.dll";

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate int Progress(nint state);

    [DllImport(Library, EntryPoint = "sqlite3_open_v2", CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static extern int Open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, out nint database, int flags, nint vfs);

    [DllImport(Library, EntryPoint = "sqlite3_close", CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static extern int Close(nint database);

    [DllImport(Library, EntryPoint = "sqlite3_db_readonly", CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static extern int ReadOnly(nint database, [MarshalAs(UnmanagedType.LPUTF8Str)] string name);

    [DllImport(Library, EntryPoint = "sqlite3_busy_timeout", CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static extern int BusyTimeout(nint database, int milliseconds);

    [DllImport(Library, EntryPoint = "sqlite3_progress_handler", CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static extern void ProgressHandler(nint database, int steps, Progress callback, nint state);

    [DllImport(Library, EntryPoint = "sqlite3_limit", CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static extern int Limit(nint database, int category, int value);

    [DllImport(Library, EntryPoint = "sqlite3_prepare_v2", CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static extern int Prepare(nint database, [MarshalAs(UnmanagedType.LPUTF8Str)] string sql, int bytes, out nint statement, nint tail);

    [DllImport(Library, EntryPoint = "sqlite3_stmt_readonly", CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static extern int StatementReadOnly(nint statement);

    [DllImport(Library, EntryPoint = "sqlite3_bind_double", CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static extern int BindDouble(nint statement, int index, double value);

    [DllImport(Library, EntryPoint = "sqlite3_step", CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static extern int Step(nint statement);

    [DllImport(Library, EntryPoint = "sqlite3_finalize", CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static extern int Finalize(nint statement);

    [DllImport(Library, EntryPoint = "sqlite3_column_double", CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static extern double Double(nint statement, int column);

    [DllImport(Library, EntryPoint = "sqlite3_column_int64", CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static extern long Integer(nint statement, int column);

    [DllImport(Library, EntryPoint = "sqlite3_column_type", CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static extern int Type(nint statement, int column);

    [DllImport(Library, EntryPoint = "sqlite3_column_text", CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static extern nint Text(nint statement, int column);
}
