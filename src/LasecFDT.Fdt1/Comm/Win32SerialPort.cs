using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace LasecFDT.Fdt1.Comm
{
    /// <summary>
    /// Porta serial mínima sobre a API Win32. Necessária porque System.IO.Ports.SerialPort só aceita nomes "COMx"
    /// e o com0com cria a outra ponta do par com nomes como "CNCB0".
    /// </summary>
    public sealed class Win32SerialPort : IDisposable
    {
        private readonly SafeFileHandle _h;
        public string Name { get; }

        public Win32SerialPort(string name, int baud, byte parity /*0 none,1 odd,2 even*/, int readTimeoutMs)
        {
            Name = name;
            _h = CreateFile(@"\\.\" + name, 0xC0000000 /*GENERIC_READ|WRITE*/, 0, IntPtr.Zero, 3 /*OPEN_EXISTING*/, 0, IntPtr.Zero);
            if (_h.IsInvalid) throw new IOException($"Não foi possível abrir {name}: {new Win32Exception(Marshal.GetLastWin32Error()).Message}");
            var dcb = new DCB { DCBlength = (uint)Marshal.SizeOf(typeof(DCB)) };
            if (!GetCommState(_h, ref dcb)) throw new Win32Exception();
            dcb.BaudRate = (uint)baud;
            dcb.ByteSize = 8;
            dcb.Parity = parity;
            dcb.StopBits = 0; // 1 stop bit
            dcb.Flags = 0x0001 | (parity != 0 ? 0x0002u : 0u) | 0x0010 /*fDtrControl=ENABLE*/ | 0x1000 /*fRtsControl=ENABLE*/; // fBinary, fParity
            if (!SetCommState(_h, ref dcb)) throw new Win32Exception();
            var to = new COMMTIMEOUTS { ReadIntervalTimeout = 20, ReadTotalTimeoutConstant = (uint)readTimeoutMs, WriteTotalTimeoutConstant = 2000 };
            if (!SetCommTimeouts(_h, ref to)) throw new Win32Exception();
        }

        /// <summary>Lê o que houver (até o timeout). Retorna 0 em timeout.</summary>
        public int Read(byte[] buf)
        {
            if (!ReadFile(_h, buf, buf.Length, out int n, IntPtr.Zero)) throw new Win32Exception();
            return n;
        }

        public void Write(byte[] data)
        {
            if (!WriteFile(_h, data, data.Length, out int n, IntPtr.Zero)) throw new Win32Exception();
            if (n != data.Length) throw new IOException($"Escrita incompleta em {Name}: {n} de {data.Length} bytes (timeout — o outro lado não está lendo?).");
        }

        public void Dispose() => _h.Dispose();

        [StructLayout(LayoutKind.Sequential)]
        private struct DCB
        {
            public uint DCBlength, BaudRate, Flags;
            public ushort wReserved, XonLim, XoffLim;
            public byte ByteSize, Parity, StopBits;
            public sbyte XonChar, XoffChar, ErrorChar, EofChar, EvtChar;
            public ushort wReserved1;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct COMMTIMEOUTS { public uint ReadIntervalTimeout, ReadTotalTimeoutMultiplier, ReadTotalTimeoutConstant, WriteTotalTimeoutMultiplier, WriteTotalTimeoutConstant; }

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr sec, uint disp, uint flags, IntPtr template);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetCommState(SafeFileHandle h, ref DCB dcb);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetCommState(SafeFileHandle h, ref DCB dcb);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetCommTimeouts(SafeFileHandle h, ref COMMTIMEOUTS t);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool ReadFile(SafeFileHandle h, byte[] buf, int len, out int read, IntPtr ov);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool WriteFile(SafeFileHandle h, byte[] buf, int len, out int written, IntPtr ov);
    }
}
