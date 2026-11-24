using System.Linq;
using System.Text;

namespace LasecFDT.Core.TypeLib
{
    /// <summary>Renderiza um <see cref="TypeLibModel"/> como Markdown estilo IDL.</summary>
    public static class TypeLibMarkdown
    {
        public static string Render(TypeLibModel lib, string title)
        {
            var sb = new StringBuilder();
            sb.AppendLine("# " + title);
            sb.AppendLine();
            sb.AppendLine("> Gerado automaticamente por `LasecFDT.Cli typelib` a partir da type library binária.");
            sb.AppendLine("> Não editar manualmente. Fonte: `" + (lib.SourcePath ?? "(registrada)") + "`.");
            sb.AppendLine();
            sb.AppendLine($"- Biblioteca: **{lib.Name}**  ");
            sb.AppendLine($"- LIBID: `{{{lib.LibId}}}`  ");
            sb.AppendLine($"- Versão: `{lib.Version}`");
            sb.AppendLine();

            var itfs = lib.Types.Where(t => t.Kind == "DISPATCH" || t.Kind == "INTERFACE").OrderBy(t => t.Name).ToList();
            sb.AppendLine("## Índice de interfaces");
            sb.AppendLine();
            sb.AppendLine("| Interface | IID | Base | Métodos |");
            sb.AppendLine("|---|---|---|---|");
            foreach (var t in itfs)
                sb.AppendLine($"| [{t.Name}](#{t.Name.ToLowerInvariant()}) | `{t.Guid}` | {t.BaseInterface} | {t.Methods.Count} |");
            sb.AppendLine();

            foreach (var t in itfs)
            {
                sb.AppendLine("### " + t.Name);
                sb.AppendLine();
                sb.AppendLine($"IID `{{{t.Guid}}}` · {(t.IsDual ? "dual" : t.Kind.ToLowerInvariant())} · base `{t.BaseInterface}`");
                if (!string.IsNullOrEmpty(t.Doc)) { sb.AppendLine(); sb.AppendLine(t.Doc); }
                sb.AppendLine();
                sb.AppendLine("```idl");
                foreach (var m in t.Methods)
                {
                    var ps = string.Join(", ", m.Parameters.Select(p =>
                        "[" + string.Join(",", new[] { p.In ? "in" : null, p.Out ? "out" : null, p.RetVal ? "retval" : null }.Where(x => x != null)) + "] " + p.Type + " " + p.Name));
                    sb.AppendLine($"[id(0x{m.DispId:x})] {m.ReturnType} {m.Name}({ps});");
                }
                sb.AppendLine("```");
                sb.AppendLine();
            }

            var others = lib.Types.Where(t => t.Kind != "DISPATCH" && t.Kind != "INTERFACE").OrderBy(t => t.Kind).ThenBy(t => t.Name).ToList();
            if (others.Count > 0)
            {
                sb.AppendLine("## Outros tipos (alias, enum, coclass, module)");
                sb.AppendLine();
                sb.AppendLine("| Tipo | Nome | GUID | Detalhe |");
                sb.AppendLine("|---|---|---|---|");
                foreach (var t in others)
                {
                    string detail = t.AliasOf != null ? "alias de " + t.AliasOf
                        : t.Implements.Count > 0 ? string.Join(", ", t.Implements.Select(i => i.Name + (i.IsDefault ? "[default]" : "") + (i.IsSource ? "[source]" : "")))
                        : string.Join(", ", t.Members.Select(mm => mm.Name + (mm.Value != null ? "=" + mm.Value : "")));
                    sb.AppendLine($"| {t.Kind} | {t.Name} | `{t.Guid}` | {detail} |");
                }
            }
            return sb.ToString();
        }
    }
}
