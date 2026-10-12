using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace PKGMerge.Core;

[SupportedOSPlatform("windows")]
internal static unsafe partial class SectorSize
{
    private const int FileStorageInfoClass = 16;

    public static uint Get(SafeFileHandle file)
    {
        FileStorageInfo info;
        if (!GetFileInformationByHandleEx(file, FileStorageInfoClass, &info, (uint)sizeof(FileStorageInfo)))
            return 0;

        return Math.Max(info.LogicalBytesPerSector, info.PhysicalBytesPerSectorForPerformance);
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetFileInformationByHandleEx(SafeFileHandle file, int infoClass, void* info, uint size);

    // https://learn.microsoft.com/pl-pl/windows/win32/api/winbase/ns-winbase-file_storage_info
    [StructLayout(LayoutKind.Sequential)]
    private struct FileStorageInfo
    {
        public uint LogicalBytesPerSector;
        public uint PhysicalBytesPerSectorForAtomicity;
        public uint PhysicalBytesPerSectorForPerformance;
        public uint FileSystemEffectivePhysicalBytesPerSectorForAtomicity;
        public uint Flags;
        public uint ByteOffsetForSectorAlignment;
        public uint ByteOffsetForPartitionAlignment;
    }
}
