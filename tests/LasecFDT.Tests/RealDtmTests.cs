using System;
using System.Linq;
using LasecFDT.Core.Project;
using LasecFDT.Fdt1.Frame;
using Xunit;

namespace LasecFDT.Tests
{
    /// <summary>
    /// Integração com DTMs reais instalados (offline: nunca chama SetCommunication nem acessa instrumentos).
    /// Pulados automaticamente quando o DTM não está registrado.
    /// </summary>
    public class RealDtmTests
    {
        private const string Ld301 = "SmarLD301DTM.DTMCore.1";      // IPersistStreamInit
        private const string Tt301 = "Smar.TT301DTM.1";             // IPersistPropertyBag
        private const string CwHart = "CWHartFdt.clsDTM";           // Comm DTM (OCX VB6)

        [DtmFact(Ld301)]
        public void Ld301_CicloCompletoOffline_ComRoundTripDePersistencia()
        {
            Sta.Run(() =>
            {
                var frame = new FrameContainer();
                var frameEvents = new System.Collections.Generic.List<FrameEvent>();
                frame.EventRaised += frameEvents.Add;
                var r = DtmProbe.Run(frame, Type.GetTypeFromProgID(Ld301).GUID, new ProbeOptions { FullInit = true });
                Assert.True(r.Created);
                Assert.True(r.EnvironmentAccepted);
                Assert.True(r.Configured);
                Assert.Equal("StreamInit", r.PersistenceKind);
                Assert.StartsWith("ok", r.RoundTrip);
                Assert.True(r.ReleasedCleanly, "OnPreparedToRelease não recebido");
                Assert.Equal("Released", r.FinalState);
                Assert.Contains(r.Steps, s => s.Step == "IDtm::Environment" && s.Ok);
                // O DTM chamou de volta o objeto Frame (callback assíncrono de §4.3.1.7), uma vez por instância:
                Assert.Equal(2, frameEvents.Count(e => e.Method == "OnPreparedToRelease"));
            });
        }

        [DtmFact(Tt301)]
        public void Tt301_PersistePorPropertyBag()
        {
            Sta.Run(() =>
            {
                var r = DtmProbe.Run(new FrameContainer(), Type.GetTypeFromProgID(Tt301).GUID, new ProbeOptions { FullInit = true });
                Assert.True(r.Configured);
                Assert.Equal("PropertyBag", r.PersistenceKind);
                Assert.StartsWith("ok", r.RoundTrip);
            });
        }

        [DtmFact(CwHart)]
        public void CodeWrightsHart_ExpoeCanalDeComunicacao()
        {
            Sta.Run(() =>
            {
                var frame = new FrameContainer();
                using (var s = DtmSession.Create(frame, Type.GetTypeFromProgID(CwHart).GUID))
                {
                    Assert.True(s.Supports("IDtm"));
                    Assert.True(s.Supports("IDtmChannel"));
                    Assert.True(s.PersistInitNew());
                    Assert.True(s.SetEnvironment());
                    var info = s.GetInformation();
                    Assert.Contains(info.DeviceTypes.SelectMany(d => d.BusCategories), b => b.IndexOf("HART", StringComparison.OrdinalIgnoreCase) >= 0);
                }
            });
        }

        [Fact]
        public void ClsidInexistente_FalhaDeFormaControlada()
        {
            Sta.Run(() =>
            {
                var s = DtmSession.Create(new FrameContainer(), Guid.NewGuid());
                Assert.Null(s.ComObject);
                Assert.Equal(DtmState.Failed, s.State);
                Assert.Contains(s.Steps, x => x.Step == "CoCreateInstance" && !x.Ok && x.HResult == "0x80040154"); // REGDB_E_CLASSNOTREG
            });
        }

        [Fact]
        public void CriarDtmForaDeSta_ELancaExcecao()
        {
            Exception ex = null;
            var t = new System.Threading.Thread(() =>
            {
                try { DtmSession.Create(new FrameContainer(), Guid.NewGuid()); } catch (Exception e) { ex = e; }
            });
            t.SetApartmentState(System.Threading.ApartmentState.MTA);
            t.Start(); t.Join();
            Assert.IsType<InvalidOperationException>(ex);
        }
    }
}
