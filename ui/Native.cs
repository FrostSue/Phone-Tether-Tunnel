using System;
using System.Collections.Generic;
using System.Net;
using System.Runtime.InteropServices;

namespace PhoneVPNDashboard
{
    internal class TcpRow
    {
        public IPAddress LocalAddress;
        public int LocalPort;
        public IPAddress RemoteAddress;
        public int RemotePort;
        public int State;
        public int Pid;
    }

    internal static class Native
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct MIB_TCPROW_OWNER_PID
        {
            public uint dwState;
            public uint dwLocalAddr;
            public uint dwLocalPort;
            public uint dwRemoteAddr;
            public uint dwRemotePort;
            public uint dwOwningPid;
        }

        private const int AF_INET = 2;
        private const int TCP_TABLE_OWNER_PID_ALL = 5;
        private const int ERROR_INSUFFICIENT_BUFFER = 122;

        [DllImport("iphlpapi.dll", SetLastError = true)]
        private static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool sort, int af, int tableClass, uint reserved);

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        /// <summary>IPv4 TCP connections with owning PIDs, without spawning netstat.</summary>
        public static List<TcpRow> GetTcpConnections()
        {
            List<TcpRow> rows = new List<TcpRow>();
            int size = 0;
            GetExtendedTcpTable(IntPtr.Zero, ref size, false, AF_INET, TCP_TABLE_OWNER_PID_ALL, 0);
            for (int attempt = 0; attempt < 3 && size > 0; attempt++)
            {
                IntPtr buf = Marshal.AllocHGlobal(size);
                try
                {
                    uint r = GetExtendedTcpTable(buf, ref size, false, AF_INET, TCP_TABLE_OWNER_PID_ALL, 0);
                    if (r == ERROR_INSUFFICIENT_BUFFER) continue;
                    if (r != 0) break;
                    int count = Marshal.ReadInt32(buf);
                    int rowSize = Marshal.SizeOf(typeof(MIB_TCPROW_OWNER_PID));
                    IntPtr ptr = new IntPtr(buf.ToInt64() + 4);
                    for (int i = 0; i < count; i++)
                    {
                        MIB_TCPROW_OWNER_PID row = (MIB_TCPROW_OWNER_PID)Marshal.PtrToStructure(ptr, typeof(MIB_TCPROW_OWNER_PID));
                        rows.Add(new TcpRow
                        {
                            LocalAddress = new IPAddress(row.dwLocalAddr),
                            LocalPort = Port(row.dwLocalPort),
                            RemoteAddress = new IPAddress(row.dwRemoteAddr),
                            RemotePort = Port(row.dwRemotePort),
                            State = (int)row.dwState,
                            Pid = (int)row.dwOwningPid
                        });
                        ptr = new IntPtr(ptr.ToInt64() + rowSize);
                    }
                    break;
                }
                finally
                {
                    Marshal.FreeHGlobal(buf);
                }
            }
            return rows;
        }

        private static int Port(uint raw)
        {
            return (int)(((raw & 0xFF) << 8) | ((raw >> 8) & 0xFF));
        }

        public static string TcpStateName(int state)
        {
            switch (state)
            {
                case 1: return "Closed";
                case 2: return "Listen";
                case 3: return "SYN sent";
                case 4: return "SYN recv";
                case 5: return "Established";
                case 6: return "FIN wait";
                case 7: return "FIN wait 2";
                case 8: return "Close wait";
                case 9: return "Closing";
                case 10: return "Last ACK";
                case 11: return "Time wait";
                case 12: return "Delete";
                default: return "?";
            }
        }

        /// <summary>Dark title bar on Windows 10 20H1+ / 11.</summary>
        public static void UseDarkTitleBar(IntPtr hwnd)
        {
            try
            {
                int on = 1;
                if (DwmSetWindowAttribute(hwnd, 20, ref on, 4) != 0) DwmSetWindowAttribute(hwnd, 19, ref on, 4);
            }
            catch { }
        }
    }
}
