using System;
using System.Runtime.ExceptionServices;
using System.Threading;
using Xunit;

namespace LasecFDT.Tests
{
    /// <summary>Executa o corpo do teste numa thread STA (DTMs FDT 1.x são objetos de apartamento).</summary>
    internal static class Sta
    {
        public static void Run(Action body)
        {
            ExceptionDispatchInfo error = null;
            var t = new Thread(() => { try { body(); } catch (Exception ex) { error = ExceptionDispatchInfo.Capture(ex); } });
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
            Assert.True(t.Join(TimeSpan.FromSeconds(120)), "Timeout na thread STA");
            error?.Throw();
        }
    }

    /// <summary>Fato que só roda se o ProgID do DTM estiver registrado nesta máquina (vista 32 bits).</summary>
    public sealed class DtmFactAttribute : FactAttribute
    {
        public DtmFactAttribute(string progId)
        {
            if (Type.GetTypeFromProgID(progId, false) == null)
                Skip = $"DTM '{progId}' não instalado nesta máquina.";
        }
    }
}
