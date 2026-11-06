using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace CodexAutoContinue.Infrastructure
{
    public sealed class SqliteDatabase : IDisposable
    {
        private IntPtr handle;
        public SqliteDatabase(string path, bool readOnly = false)
        {
            var result = Native.sqlite3_open_v2(Utf8(path), out handle, (readOnly ? 1 : 6) | 0x10000, IntPtr.Zero);
            if (result != 0) { Dispose(); throw new InvalidOperationException("无法打开 SQLite 数据库。"); }
            Native.sqlite3_busy_timeout(handle, 1500);
        }
        public List<Dictionary<string, object>> Query(string sql, params object[] parameters)
        {
            return QueryRows(sql, parameters).ToList();
        }
        public IEnumerable<Dictionary<string, object>> QueryRows(string sql, params object[] parameters)
        {
            using (var statement = Prepare(sql, parameters))
            {
                int result;
                while ((result = Native.sqlite3_step(statement.Handle)) == 100)
                {
                    var row = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                    for (var i = 0; i < Native.sqlite3_column_count(statement.Handle); i++)
                    {
                        var name = Text(Native.sqlite3_column_name(statement.Handle, i));
                        var type = Native.sqlite3_column_type(statement.Handle, i);
                        row[name] = type == 5 ? null : type == 1 ? (object)Native.sqlite3_column_int64(statement.Handle, i) : type == 2 ? (object)Native.sqlite3_column_double(statement.Handle, i) : Text(Native.sqlite3_column_text(statement.Handle, i));
                    }
                    yield return row;
                }
                if (result != 101) throw new InvalidOperationException("SQLite 查询失败（错误码 " + result + "）。");
            }
        }
        public int Execute(string sql, params object[] parameters)
        {
            Query(sql, parameters);
            return Native.sqlite3_changes(handle);
        }
        private Statement Prepare(string sql, object[] parameters)
        {
            IntPtr value;
            if (Native.sqlite3_prepare_v2(handle, Utf8(sql), -1, out value, IntPtr.Zero) != 0)
                throw new InvalidOperationException("SQLite 语句与当前数据库版本不兼容。");
            var statement = new Statement(value);
            try
            {
                for (var i = 0; i < parameters.Length; i++)
                {
                    var item = parameters[i];
                    int result;
                    if (item == null) result = Native.sqlite3_bind_null(value, i + 1);
                    else if (item is int || item is long || item is bool) result = Native.sqlite3_bind_int64(value, i + 1, Convert.ToInt64(item));
                    else
                    {
                        var bytes = Utf8(Convert.ToString(item, System.Globalization.CultureInfo.InvariantCulture));
                        result = Native.sqlite3_bind_text(value, i + 1, bytes, bytes.Length - 1, new IntPtr(-1));
                    }
                    if (result != 0) throw new InvalidOperationException("SQLite 参数绑定失败。");
                }
                return statement;
            }
            catch { statement.Dispose(); throw; }
        }
        private static byte[] Utf8(string value) { return Encoding.UTF8.GetBytes(value + "\0"); }
        private static string Text(IntPtr value)
        {
            if (value == IntPtr.Zero) return null;
            var length = 0;
            while (Marshal.ReadByte(value, length) != 0) length++;
            var data = new byte[length]; Marshal.Copy(value, data, 0, length);
            return Encoding.UTF8.GetString(data);
        }
        public void Dispose() { if (handle != IntPtr.Zero) { Native.sqlite3_close_v2(handle); handle = IntPtr.Zero; } }
        private sealed class Statement : IDisposable
        {
            public IntPtr Handle { get; private set; }
            public Statement(IntPtr handle) { Handle = handle; }
            public void Dispose() { if (Handle != IntPtr.Zero) { Native.sqlite3_finalize(Handle); Handle = IntPtr.Zero; } }
        }
        private static class Native
        {
            private const string Library = "winsqlite3.dll";
            [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_open_v2(byte[] path, out IntPtr db, int flags, IntPtr vfs);
            [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_close_v2(IntPtr db);
            [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_busy_timeout(IntPtr db, int ms);
            [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_prepare_v2(IntPtr db, byte[] sql, int n, out IntPtr stmt, IntPtr tail);
            [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_step(IntPtr stmt);
            [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_finalize(IntPtr stmt);
            [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_column_count(IntPtr stmt);
            [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_column_type(IntPtr stmt, int column);
            [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr sqlite3_column_name(IntPtr stmt, int column);
            [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr sqlite3_column_text(IntPtr stmt, int column);
            [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern long sqlite3_column_int64(IntPtr stmt, int column);
            [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern double sqlite3_column_double(IntPtr stmt, int column);
            [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_bind_text(IntPtr stmt, int index, byte[] value, int n, IntPtr destroy);
            [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_bind_int64(IntPtr stmt, int index, long value);
            [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_bind_null(IntPtr stmt, int index);
            [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_changes(IntPtr db);
        }
    }
}
