using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using LasecFDT.Core.Rpc;

namespace LasecFDT.Host
{
    /// <summary>
    /// Identidade da estação que este Host atende. Num servidor de terminal (thin clients) várias estações rodam na
    /// MESMA máquina, muitas vezes com a MESMA conta genérica: por isso o isolamento é por sessão do Windows e pelo nome
    /// do thin client (WTSClientName), nunca só pelo usuário. Pode ser forçada com LASECFDT_STATION (testes, quiosques).
    /// </summary>
    public static class Station
    {
        public static StationInfo Detect()
        {
            int session = Process.GetCurrentProcess().SessionId;
            string client = QueryClientName();
            bool remote = GetSystemMetrics(0x1000 /*SM_REMOTESESSION*/) != 0;
            string id = Environment.GetEnvironmentVariable("LASECFDT_STATION");
            if (string.IsNullOrWhiteSpace(id)) id = !string.IsNullOrWhiteSpace(client) ? client : Environment.MachineName;
            return new StationInfo
            {
                Id = Sanitize(id),
                ClientName = client,
                SessionId = session,
                User = Environment.UserDomainName + "\\" + Environment.UserName,
                Machine = Environment.MachineName,
                IsRemote = remote,
            };
        }

        /// <summary>Nome do computador cliente da sessão (thin client). Vazio na sessão de console.</summary>
        public static string QueryClientName()
        {
            try
            {
                if (!WTSQuerySessionInformation(IntPtr.Zero, -1 /*WTS_CURRENT_SESSION*/, 10 /*WTSClientName*/, out IntPtr buf, out int bytes)) return null;
                try { return bytes > 2 ? Marshal.PtrToStringUni(buf)?.Trim() : null; }
                finally { WTSFreeMemory(buf); }
            }
            catch { return null; }
        }

        public static string Sanitize(string s)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var r = new string((s ?? "station").Trim().Select(c => invalid.Contains(c) || char.IsWhiteSpace(c) ? '_' : c).ToArray());
            return r.Length == 0 ? "station" : r.ToUpperInvariant();
        }

        [DllImport("wtsapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool WTSQuerySessionInformation(IntPtr server, int sessionId, int infoClass, out IntPtr buffer, out int bytesReturned);
        [DllImport("wtsapi32.dll")] private static extern void WTSFreeMemory(IntPtr memory);
        [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    }
}
