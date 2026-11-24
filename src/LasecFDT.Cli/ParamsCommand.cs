using System;
using System.Threading.Tasks;
using LasecFDT.Core.Rpc;

namespace LasecFDT.Cli
{
    internal static partial class Program
    {
        /// <summary>
        /// lasecfdt params &lt;DTM&gt; [--path XPATH] [--out arquivo.xml]
        /// Parâmetros offline do DTM (IDtmParameter::GetParameters, DTMParameterSchema), num worker isolado. Somente leitura.
        /// </summary>
        private static int CmdParams(string[] args)
        {
            if (args.Length < 2) return Usage();
            return Run(async () =>
            {
                using (var sup = NewSupervisor())
                {
                    var i = await sup.CreateAsync(new CreateDtmParams { Dtm = args[1] });
                    try
                    {
                        foreach (var path in Opt(args, "--path") != null ? new[] { Opt(args, "--path") } : new[] { "/FDT", "", "/", "FDT" })
                        {
                            try
                            {
                                string xml = await sup.GetParametersAsync(i, path);
                                Console.WriteLine($"--- GetParameters(\"{path}\"): {(xml == null ? "null" : xml.Length + " caracteres")}");
                                if (xml != null && Opt(args, "--out") != null) { System.IO.File.WriteAllText(Opt(args, "--out"), xml); Console.WriteLine("gravado em " + Opt(args, "--out")); }
                                else if (xml != null) Console.WriteLine(xml.Length > 1500 ? xml.Substring(0, 1500) + " …" : xml);
                                if (xml != null) break;
                            }
                            catch (RpcException ex) { Console.WriteLine($"--- GetParameters(\"{path}\"): ERRO {ex.Message}"); }
                        }
                    }
                    finally { await sup.ReleaseAsync(i); }
                    return 0;
                }
            });
        }
    }
}
