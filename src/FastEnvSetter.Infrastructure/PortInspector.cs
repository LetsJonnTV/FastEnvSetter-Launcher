using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using FastEnvSetter.Core;

namespace FastEnvSetter.Infrastructure;

public sealed partial class PortInspector : IPortInspector
{
    private const int AfInet = 2;
    private const int AfInet6 = 23;
    private const int OwnerPidListener = 3;
    private const int Ipv4RowSize = 24;
    private const int Ipv6RowSize = 56;

    public PortOwner? FindOwner(int port)
    {
        var processId = FindProcessId(port, AfInet, Ipv4RowSize, portOffset: 8, pidOffset: 20)
            ?? FindProcessId(port, AfInet6, Ipv6RowSize, portOffset: 20, pidOffset: 52);
        return processId is { } id ? new PortOwner(port, id, ProcessName(id)) : null;
    }

    private static string ProcessName(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return process.ProcessName;
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException)
        {
            return "unbekannt";
        }
    }

    private static int? FindProcessId(int port, int family, int rowSize, int portOffset, int pidOffset)
    {
        var size = 0;
        GetExtendedTcpTable(0, ref size, false, family, OwnerPidListener, 0);
        if (size == 0)
            return null;

        var table = Marshal.AllocHGlobal(size);
        try
        {
            if (GetExtendedTcpTable(table, ref size, false, family, OwnerPidListener, 0) != 0)
                return null;

            var count = Marshal.ReadInt32(table);
            for (var i = 0; i < count; i++)
            {
                var row = table + 4 + (i * rowSize);
                var rowPort = (ushort)IPAddress.NetworkToHostOrder((short)Marshal.ReadInt16(row + portOffset));
                if (rowPort == port)
                    return Marshal.ReadInt32(row + pidOffset);
            }

            return null;
        }
        finally
        {
            Marshal.FreeHGlobal(table);
        }
    }

    [LibraryImport("iphlpapi.dll")]
    private static partial uint GetExtendedTcpTable(
        nint table, ref int size, [MarshalAs(UnmanagedType.Bool)] bool order, int family, int tableClass, uint reserved);
}
