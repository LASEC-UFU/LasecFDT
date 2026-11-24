using System;
using System.IO;
using System.Runtime.InteropServices;

namespace LasecFDT.Fdt1.Frame
{
    /// <summary>
    /// Valida documentos FDT 1.x contra os schemas XDR (x-schema:) usando o MSXML 3 do Windows — o mesmo
    /// parser que os DTMs COM usam (o System.Xml do .NET não suporta XDR). Usado nos testes para garantir
    /// que todo XML emitido pelo Frame é aceito pelos DTMs (ex.: erro 0xC00CE015 visto no TT301 com atributo
    /// "tag" sem o prefixo fdt:).
    /// </summary>
    public sealed class XdrValidator
    {
        private readonly string _dir;

        /// <param name="schemaDir">Diretório com os schemas FDT (ex.: FrameContainer.SchemaPath).</param>
        public XdrValidator(string schemaDir)
        {
            // Cópia dos schemas num diretório temporário: o documento é gravado ao lado deles para que
            // as referências relativas "x-schema:Nome.xml" sejam resolvidas.
            _dir = Path.Combine(Path.GetTempPath(), "LasecFDT-xdr-" + Math.Abs(schemaDir.ToLowerInvariant().GetHashCode()).ToString("X"));
            Directory.CreateDirectory(_dir);
            foreach (var f in Directory.GetFiles(schemaDir))
            {
                var dst = Path.Combine(_dir, Path.GetFileName(f));
                if (!File.Exists(dst) || File.GetLastWriteTimeUtc(dst) < File.GetLastWriteTimeUtc(f)) File.Copy(f, dst, true);
            }
        }

        /// <summary>Retorna null se válido; senão a mensagem de erro do MSXML (com código e posição).</summary>
        public string Validate(string xml)
        {
            string file = Path.Combine(_dir, "doc-" + Guid.NewGuid().ToString("N") + ".xml");
            File.WriteAllText(file, xml);
            object dom = null;
            try
            {
                dom = Activator.CreateInstance(Type.GetTypeFromProgID("Msxml2.DOMDocument.3.0", true));
                dynamic d = dom;
                d.async = false;
                d.validateOnParse = true;
                d.resolveExternals = true;
                bool ok = d.load(file);
                if (ok) return null;
                dynamic e = d.parseError;
                return $"0x{((int)e.errorCode):X8} linha {e.line} col {e.linepos}: {((string)e.reason).Trim()}";
            }
            finally
            {
                if (dom != null) Marshal.FinalReleaseComObject(dom);
                try { File.Delete(file); } catch { }
            }
        }
    }
}
