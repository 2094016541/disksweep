using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Sweep.Core;

/// <summary>只封装实际用到的 Win32 API，避免引入整套 P/Invoke 依赖（文档 2.3）。</summary>
internal static partial class NativeMethods
{
    internal const uint FILE_READ_ATTRIBUTES = 0x0000;
    internal const uint FILE_SHARE_ALL = 0x0001 | 0x0002 | 0x0004;
    internal const uint OPEN_EXISTING = 3;
    internal const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;
    internal const uint FILE_NAME_NORMALIZED = 0x0000;
    internal const uint VOLUME_NAME_DOS = 0x0000;
    internal const uint FILE_ATTRIBUTE_REPARSE_POINT = 0x400;

    internal const uint FSCTL_ENUM_USN_DATA = 0x900B3;
    internal const uint FSCTL_GET_NTFS_VOLUME_DATA = 0x90064;
    internal const int MaxPath = 32768;

    /// <summary>列目录所需的最小访问权；FILE_LIST_DIRECTORY 才能枚举子项。</summary>
    internal const uint FILE_LIST_DIRECTORY = 0x0001;
    /// <summary>FILE_INFORMATION_CLASS.FileIdBothDirectoryInfo / RestartInfo。</summary>
    internal const int FileIdBothDirectoryInfo = 10;
    internal const int FileIdBothDirectoryRestartInfo = 11;
    internal const int ErrorNoMoreFiles = 18;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "GetLongPathNameW")]
    internal static extern uint GetLongPathName(string shortPath, StringBuilder longPath, uint bufferSize);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateFileW")]
    internal static extern SafeFileHandle CreateFile(
        string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes,
        uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "GetFinalPathNameByHandleW")]
    internal static extern uint GetFinalPathNameByHandle(
        SafeFileHandle file, StringBuilder path, uint pathLength, uint flags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern bool GetDiskFreeSpaceEx(
        string directoryName, out ulong freeBytesAvailable, out ulong totalBytes, out ulong totalFreeBytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool GetFileInformationByHandle(SafeFileHandle file, out ByHandleFileInformation info);

    /// <summary>一次拿到整目录的文件 ID / 大小 / 属性 / 时间，避免逐文件 open。</summary>
    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool GetFileInformationByHandleEx(
        SafeFileHandle file, int infoClass, IntPtr buffer, uint bufferSize);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "GetCompressedFileSizeW")]
    internal static extern uint GetCompressedFileSize(string fileName, out uint fileSizeHigh);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "FindFirstFileExW")]
    internal static extern IntPtr FindFirstFileEx(
        string fileName, int infoLevelId, out Win32FindData findFileData,
        int searchOp, IntPtr searchFilter, uint additionalFlags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "FindNextFileW")]
    internal static extern bool FindNextFile(IntPtr findFile, out Win32FindData findFileData);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool FindClose(IntPtr findFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool DeviceIoControl(
        SafeFileHandle device, uint ioControlCode, IntPtr inBuffer, uint inBufferSize,
        IntPtr outBuffer, uint outBufferSize, out uint bytesReturned, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool CloseHandle(IntPtr handle);

    internal const uint FO_DELETE = 3;
    internal const ushort FOF_SILENT = 0x0004;
    internal const ushort FOF_NOCONFIRMATION = 0x0010;
    internal const ushort FOF_ALLOWUNDO = 0x0040;
    internal const ushort FOF_NOCONFIRMMKDIR = 0x0200;
    internal const ushort FOF_NOERRORUI = 0x0400;
    internal const ushort FOF_NO_UI = FOF_SILENT | FOF_NOCONFIRMATION | FOF_NOERRORUI | FOF_NOCONFIRMMKDIR;
    internal const int DE_OPCANCELLED = 0x4C7;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "SHFileOperationW")]
    internal static extern int SHFileOperation(ref ShFileOpStruct fileOp);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "SHQueryRecycleBinW")]
    internal static extern int SHQueryRecycleBin(string rootPath, ref ShQueryRbInfo info);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct Win32FindData
    {
        public uint dwFileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME ftCreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME ftLastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME ftLastWriteTime;
        public uint nFileSizeHigh;
        public uint nFileSizeLow;
        public uint dwReserved0;
        public uint dwReserved1;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string cFileName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)] public string cAlternateFileName;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct ByHandleFileInformation
    {
        public uint dwFileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME ftCreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME ftLastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME ftLastWriteTime;
        public uint dwVolumeSerialNumber;
        public uint nFileSizeHigh;
        public uint nFileSizeLow;
        public uint nNumberOfLinks;
        public uint nFileIndexHigh;
        public uint nFileIndexLow;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct ShFileOpStruct
    {
        public IntPtr hwnd;
        public uint wFunc;
        [MarshalAs(UnmanagedType.LPWStr)] public string pFrom;
        [MarshalAs(UnmanagedType.LPWStr)] public string pTo;
        public ushort fFlags;
        public int fAnyOperationsAborted;
        public IntPtr hNameMappings;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszProgressTitle;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct ShQueryRbInfo
    {
        public uint cbSize;
        public long i64Size;
        public long i64NumItems;
    }
}
