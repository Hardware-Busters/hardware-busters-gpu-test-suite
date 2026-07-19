using System.Globalization;
using System.Runtime.InteropServices;

namespace GpuSuite.Engine.Diagnostics;

/// <summary>Minimal read-only wrapper around Windows' inbox winsqlite3.dll.</summary>
internal static class WindowsSqliteReader
{
    private const int SqliteOpenReadOnly = 0x00000001;
    private const int SqliteRow = 100;
    private const int SqliteDone = 101;
    private const int SqliteNull = 5;

    public static bool TryQueryFirst(string databasePath, string sql, out string?[] row, out string error)
    {
        row = [];
        error = "";
        IntPtr db = IntPtr.Zero;
        IntPtr statement = IntPtr.Zero;
        try
        {
            int rc = sqlite3_open_v2(databasePath, out db, SqliteOpenReadOnly, IntPtr.Zero);
            if (rc != 0)
            {
                error = Error(db, rc);
                return false;
            }

            rc = sqlite3_prepare_v2(db, sql, -1, out statement, IntPtr.Zero);
            if (rc != 0)
            {
                error = Error(db, rc);
                return false;
            }

            rc = sqlite3_step(statement);
            if (rc == SqliteDone)
            {
                error = "query returned no rows";
                return false;
            }
            if (rc != SqliteRow)
            {
                error = Error(db, rc);
                return false;
            }

            row = new string?[sqlite3_column_count(statement)];
            for (int i = 0; i < row.Length; i++)
            {
                if (sqlite3_column_type(statement, i) == SqliteNull) continue;
                IntPtr text = sqlite3_column_text(statement, i);
                row[i] = text == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(text);
            }
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
        finally
        {
            if (statement != IntPtr.Zero) sqlite3_finalize(statement);
            if (db != IntPtr.Zero) sqlite3_close(db);
        }
    }

    private static string Error(IntPtr db, int rc)
    {
        if (db == IntPtr.Zero) return $"SQLite error {rc.ToString(CultureInfo.InvariantCulture)}";
        IntPtr value = sqlite3_errmsg(db);
        return value == IntPtr.Zero ? $"SQLite error {rc}" : Marshal.PtrToStringUTF8(value) ?? $"SQLite error {rc}";
    }

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
    private static extern int sqlite3_open_v2([MarshalAs(UnmanagedType.LPUTF8Str)] string filename,
        out IntPtr database, int flags, IntPtr vfs);

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_close(IntPtr database);

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
    private static extern int sqlite3_prepare_v2(IntPtr database, [MarshalAs(UnmanagedType.LPUTF8Str)] string sql,
        int byteCount, out IntPtr statement, IntPtr tail);

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_step(IntPtr statement);

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_finalize(IntPtr statement);

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_column_count(IntPtr statement);

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_column_type(IntPtr statement, int column);

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr sqlite3_column_text(IntPtr statement, int column);

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr sqlite3_errmsg(IntPtr database);
}
