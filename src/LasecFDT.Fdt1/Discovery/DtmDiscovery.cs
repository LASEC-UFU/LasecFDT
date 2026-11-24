using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using LasecFDT.Core;
using LasecFDT.Core.Catalog;
using LasecFDT.Fdt1.Com;
using Microsoft.Win32;

namespace LasecFDT.Fdt1.Discovery
{
    /// <summary>
    /// Descoberta de DTMs instalados (Etapa A).
    ///
    /// Critério (FDT 1.2.1 §7.1.1, §7.1.2, §7.1.5): é DTM toda classe COM registrada na categoria
    /// CATID_FDT_DTM {036D1490-…}; BTMs usam CATID_FDT_BTM {036D1690-…}. Nenhum outro componente COM
    /// é tratado como DTM. Classes com outras categorias FDT (canal, apresentação) são listadas à parte.
    ///
    /// A varredura lê diretamente as duas vistas do registro (32 e 64 bits), porque um processo só
    /// enxerga uma delas via ICatInformation. O ICatInformation é usado como verificação cruzada.
    /// Nada é gravado no registro.
    /// </summary>
    public static class DtmDiscovery
    {
        public static DtmCatalog Scan(bool readTypeLibs = true)
        {
            var catalog = new DtmCatalog
            {
                GeneratedAtUtc = DateTime.UtcNow,
                MachineName = Environment.MachineName,
                OsIs64Bit = Environment.Is64BitOperatingSystem,
                ScannerProcessArch = Environment.Is64BitProcess ? "x64" : "x86",
            };

            var views = Environment.Is64BitOperatingSystem
                ? new[] { RegistryView.Registry32, RegistryView.Registry64 }
                : new[] { RegistryView.Registry32 };

            ReadCatalogUpdates(catalog, views);
            catalog.FdtTypeLibrary = ReadFdtTypeLibStatus(views);

            FdtInterfaceTable fdtTable = null;
            try { fdtTable = FdtInterfaceTable.Default; } catch { /* sem tabela, análise estática limitada */ }

            foreach (var view in views)
            {
                using (var hkcr = RegistryKey.OpenBaseKey(RegistryHive.ClassesRoot, view))
                {
                    var catNames = ReadFdtCategoryNames(hkcr, view, catalog);
                    using (var clsidRoot = hkcr.OpenSubKey("CLSID"))
                    {
                        if (clsidRoot == null) continue;
                        foreach (var clsidName in clsidRoot.GetSubKeyNames())
                        {
                            List<Guid> cats;
                            using (var ic = clsidRoot.OpenSubKey(clsidName + "\\Implemented Categories"))
                            {
                                if (ic == null) continue;
                                cats = ic.GetSubKeyNames().Select(TryGuid).Where(g => g.HasValue).Select(g => g.Value).ToList();
                            }
                            var fdtCats = cats.Where(c => IsFdtCategory(c, catNames)).ToList();
                            if (fdtCats.Count == 0) continue;

                            using (var cls = clsidRoot.OpenSubKey(clsidName))
                            {
                                var entry = BuildEntry(clsidName, cls, view, cats, catNames, fdtTable, readTypeLibs);
                                bool isDtmOrBtm = cats.Contains(FdtIds.CatidFdtDtm) || cats.Contains(FdtIds.CatidFdtBtm);
                                (isDtmOrBtm ? catalog.Dtms : catalog.RelatedFdtObjects).Add(entry);
                            }
                        }
                    }
                }
            }

            catalog.CategoryManagerCrossCheck = CrossCheckWithCategoryManager(catalog);
            catalog.Dtms = catalog.Dtms.OrderBy(d => d.Server?.CompanyName).ThenBy(d => d.Name).ToList();
            return catalog;
        }

        // ---------------------------------------------------------------- categorias

        private static bool IsFdtCategory(Guid c, Dictionary<Guid, string> names)
        {
            if (FdtIds.RoleByCategory.ContainsKey(c) || FdtIds.FieldbusByCategory.ContainsKey(c)) return true;
            // Categorias registradas com prefixo "FDT " (ex.: "FDT FIELDBUS FF H1") também são FDT.
            return names.TryGetValue(c, out var n) && n.StartsWith("FDT ", StringComparison.OrdinalIgnoreCase);
        }

        private static Dictionary<Guid, string> ReadFdtCategoryNames(RegistryKey hkcr, RegistryView view, DtmCatalog catalog)
        {
            var result = new Dictionary<Guid, string>();
            using (var cc = hkcr.OpenSubKey("Component Categories"))
            {
                if (cc == null) return result;
                foreach (var name in cc.GetSubKeyNames())
                {
                    var g = TryGuid(name);
                    if (!g.HasValue) continue;
                    using (var k = cc.OpenSubKey(name))
                    {
                        // Descrição: valor com nome de LCID hex ("409") ou "0"/default.
                        string desc = k.GetValueNames().Select(v => k.GetValue(v) as string).FirstOrDefault(s => !string.IsNullOrEmpty(s));
                        if (desc == null) continue;
                        result[g.Value] = desc;
                        if (desc.StartsWith("FDT ", StringComparison.OrdinalIgnoreCase) || FdtIds.RoleByCategory.ContainsKey(g.Value))
                        {
                            string v = ViewName(view);
                            if (!catalog.FdtCategories.Any(c => c.Catid == FdtIds.ToFdtUuid(g.Value) && c.RegistryView == v))
                                catalog.FdtCategories.Add(new RegisteredCategory { Catid = FdtIds.ToFdtUuid(g.Value), Name = desc, RegistryView = v });
                        }
                    }
                }
            }
            return result;
        }

        // ---------------------------------------------------------------- entrada

        private static DtmCatalogEntry BuildEntry(string clsidName, RegistryKey cls, RegistryView view, List<Guid> cats,
            Dictionary<Guid, string> catNames, FdtInterfaceTable fdtTable, bool readTypeLibs)
        {
            var e = new DtmCatalogEntry
            {
                Clsid = FdtIds.ToFdtUuid(TryGuid(clsidName) ?? Guid.Empty),
                Name = cls.GetValue("") as string,
                ProgId = SubDefault(cls, "ProgID") ?? SubDefault(cls, "ProgId"),
                VersionIndependentProgId = SubDefault(cls, "VersionIndependentProgID"),
                RegistryView = ViewName(view),
            };
            foreach (var c in cats)
            {
                catNames.TryGetValue(c, out var n);
                e.Categories.Add(new RegisteredCategory { Catid = FdtIds.ToFdtUuid(c), Name = n });
                if (FdtIds.RoleByCategory.TryGetValue(c, out var role)) e.Roles.Add(role);
                if (FdtIds.FieldbusByCategory.TryGetValue(c, out var bus)) e.Fieldbuses.Add(bus);
                else if (n != null && n.StartsWith("FDT FIELDBUS ", StringComparison.OrdinalIgnoreCase)) e.Fieldbuses.Add(n.Substring(13));
            }

            e.Server = ReadServer(cls, view, e.Issues);
            if (e.Server?.Kind == "InprocServer32")
            {
                e.RequiredHostArch = view == RegistryView.Registry64 ? "x64" : "x86";
                if (!e.Server.IsManaged && e.Server.PeMachine != null && e.Server.PeMachine != e.RequiredHostArch)
                    e.Issues.Add($"Arquitetura do binário ({e.Server.PeMachine}) difere da vista do registro ({e.RequiredHostArch}).");
                if (!string.Equals(e.Server.ThreadingModel, "Apartment", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(e.Server.ThreadingModel, "Both", StringComparison.OrdinalIgnoreCase))
                    e.Issues.Add($"ThreadingModel '{e.Server.ThreadingModel ?? "(ausente = single-threaded)"}': exige a thread STA principal do host.");
            }
            else if (e.Server?.Kind == "LocalServer32") e.RequiredHostArch = "any (out-of-process)";

            e.TypeLib = ReadTypeLibRef(cls, view);
            if (readTypeLibs) FillDeclaredInterfaces(e, fdtTable);
            return e;
        }

        private static ComServerInfo ReadServer(RegistryKey cls, RegistryView view, List<string> issues)
        {
            foreach (var kind in new[] { "InprocServer32", "LocalServer32" })
            {
                using (var k = cls.OpenSubKey(kind))
                {
                    if (k == null) continue;
                    var s = new ComServerInfo { Kind = kind };
                    s.RegisteredPath = k.GetValue("", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
                    s.ThreadingModel = k.GetValue("ThreadingModel") as string;
                    // Valor nomeado igual à chave = descritor "darwin" de instalação MSI anunciada.
                    s.MsiAdvertised = k.GetValue(kind) != null;
                    s.ManagedClass = k.GetValue("Class") as string;
                    s.ManagedAssembly = k.GetValue("Assembly") as string;
                    s.ManagedRuntimeVersion = k.GetValue("RuntimeVersion") as string;
                    s.ManagedCodeBase = k.GetValue("CodeBase") as string;
                    s.IsManaged = s.RegisteredPath != null && s.RegisteredPath.Trim('"').EndsWith("mscoree.dll", StringComparison.OrdinalIgnoreCase);

                    string path = kind == "LocalServer32" ? ExtractExe(s.RegisteredPath) : s.RegisteredPath?.Trim('"');
                    if (s.IsManaged)
                    {
                        string cbPath = CodeBaseToPath(s.ManagedCodeBase);
                        s.ManagedCodeBaseExists = cbPath != null ? File.Exists(cbPath) : (bool?)null;
                        if (cbPath == null) issues.Add("DTM gerenciado sem CodeBase: o assembly precisa estar no GAC.");
                        else if (s.ManagedCodeBaseExists == false) issues.Add("CodeBase do assembly .NET não existe: " + cbPath);
                        if (s.ManagedRuntimeVersion != null && s.ManagedRuntimeVersion.StartsWith("v4"))
                            issues.Add("DTM .NET Framework (CLR v4): o host precisa ser processo .NET Framework 4.x (não .NET 5+).");
                        path = cbPath ?? path;
                    }
                    s.ResolvedPath = ResolvePath(path, view);
                    s.FileExists = s.ResolvedPath != null && File.Exists(s.ResolvedPath);
                    if (!s.FileExists && !s.IsManaged) issues.Add("Binário do servidor COM não encontrado: " + (s.ResolvedPath ?? s.RegisteredPath));
                    if (s.FileExists)
                    {
                        s.PeMachine = ReadPeMachine(s.ResolvedPath);
                        try
                        {
                            var fv = FileVersionInfo.GetVersionInfo(s.ResolvedPath);
                            s.CompanyName = Clean(fv.CompanyName); s.ProductName = Clean(fv.ProductName);
                            s.FileDescription = Clean(fv.FileDescription);
                            s.FileVersion = Clean(fv.FileVersion); s.ProductVersion = Clean(fv.ProductVersion);
                        }
                        catch { /* sem recurso de versão (viola §7.1.4) */ }
                        if (s.FileVersion == null) issues.Add("Binário sem recurso de versão (FDT 1.2.1 §7.1.4 exige).");
                    }
                    return s;
                }
            }
            issues.Add("Classe sem InprocServer32/LocalServer32.");
            return null;
        }

        private static TypeLibRef ReadTypeLibRef(RegistryKey cls, RegistryView view)
        {
            var libId = SubDefault(cls, "TypeLib");
            if (string.IsNullOrEmpty(libId)) return null;
            var r = new TypeLibRef { LibId = libId.Trim('{', '}').ToUpperInvariant() };
            using (var hkcr = RegistryKey.OpenBaseKey(RegistryHive.ClassesRoot, view))
            using (var tl = hkcr.OpenSubKey("TypeLib\\{" + r.LibId + "}"))
            {
                if (tl == null) return r;
                var ver = SubDefault(cls, "Version");
                var versions = tl.GetSubKeyNames();
                r.Version = versions.Contains(ver) ? ver : versions.OrderByDescending(v => v, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
                if (r.Version == null) return r;
                using (var v = tl.OpenSubKey(r.Version))
                {
                    var lcid = v?.GetSubKeyNames().FirstOrDefault(n => n.All(char.IsDigit));
                    if (lcid != null)
                    {
                        string plat = view == RegistryView.Registry64 ? "win64" : "win32";
                        r.Path = v.OpenSubKey(lcid + "\\" + plat)?.GetValue("") as string
                              ?? v.OpenSubKey(lcid + "\\win32")?.GetValue("") as string;
                        r.Path = ResolvePath(r.Path?.Trim('"'), view);
                        r.FileExists = r.Path != null && File.Exists(StripResourceIndex(r.Path));
                    }
                }
            }
            return r;
        }

        private static void FillDeclaredInterfaces(DtmCatalogEntry e, FdtInterfaceTable fdt)
        {
            if (e.TypeLib?.Path == null || !e.TypeLib.FileExists)
            {
                e.DeclaredInterfacesNote = e.Server?.IsManaged == true
                    ? "DTM gerenciado sem type library registrada: interfaces só verificáveis por sonda (probe)."
                    : "Type library não registrada/encontrada: interfaces só verificáveis por sonda (probe).";
                return;
            }
            try
            {
                var tl = TypeLibInspector.Load(e.TypeLib.Path);
                var coclass = tl.Types.FirstOrDefault(t => t.Kind == "COCLASS" && string.Equals(t.Guid, e.Clsid, StringComparison.OrdinalIgnoreCase));
                if (coclass == null)
                {
                    e.DeclaredInterfacesNote = "Coclass não encontrada na type library do DTM.";
                    return;
                }
                e.DeclaredInterfaces = coclass.Implements.Select(i => new InterfaceRef
                {
                    Name = i.Name, Iid = i.Iid, IsSource = i.IsSource,
                    IsFdt = fdt != null && fdt.IsFdt(new Guid(i.Iid)),
                }).ToList();
                e.DeclaredInterfacesNote = "Declaração estática da type library; a confirmação real é feita por QueryInterface na sonda.";
            }
            catch (Exception ex)
            {
                e.DeclaredInterfacesNote = "Falha ao ler type library: " + ex.Message;
            }
        }

        // ---------------------------------------------------------------- verificação cruzada

        private static CategoryManagerCrossCheck CrossCheckWithCategoryManager(DtmCatalog catalog)
        {
            string myView = Environment.Is64BitProcess ? "64" : "32";
            var check = new CategoryManagerCrossCheck { ProcessView = myView };
            ICatInformation ci = null;
            IEnumGUID en = null;
            try
            {
                var t = Type.GetTypeFromCLSID(ComCategories.ClsidStdComponentCategoriesMgr, true);
                ci = (ICatInformation)Activator.CreateInstance(t);
                ci.EnumClassesOfCategories(1, new[] { FdtIds.CatidFdtDtm }, -1, null, out en);
                var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var buf = new Guid[16];
                while (true)
                {
                    int hr = en.Next(buf.Length, buf, out int got);
                    for (int i = 0; i < got; i++) found.Add(FdtIds.ToFdtUuid(buf[i]));
                    if (hr != 0 || got == 0) break; // S_FALSE = fim
                }
                check.ClassesFound = found.Count;
                var scanned = new HashSet<string>(catalog.Dtms.Where(d => d.RegistryView == myView && d.Roles.Contains("Dtm")).Select(d => d.Clsid), StringComparer.OrdinalIgnoreCase);
                check.MissingFromRegistryScan = found.Where(f => !scanned.Contains(f)).ToList();
                check.MissingFromCategoryManager = scanned.Where(s => !found.Contains(s)).ToList();
            }
            catch (Exception ex) { check.Error = ex.Message; }
            finally
            {
                if (en != null) Marshal.ReleaseComObject(en);
                if (ci != null) Marshal.ReleaseComObject(ci);
            }
            return check;
        }

        // ---------------------------------------------------------------- registro FDT

        private static void ReadCatalogUpdates(DtmCatalog catalog, RegistryView[] views)
        {
            foreach (var view in views)
                using (var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view))
                using (var k = hklm.OpenSubKey(@"SOFTWARE\FDT\DTMCatalogUpdates"))
                {
                    if (k == null) continue;
                    catalog.CatalogModificationFlag = k.GetValue("ModificationFlag") as string;
                    catalog.CatalogModificationComment = k.GetValue("ModificationComment") as string;
                    return;
                }
        }

        private static FdtTypeLibStatus ReadFdtTypeLibStatus(RegistryView[] views)
        {
            var st = new FdtTypeLibStatus();
            foreach (var view in views)
                using (var hkcr = RegistryKey.OpenBaseKey(RegistryHive.ClassesRoot, view))
                using (var tl = hkcr.OpenSubKey("TypeLib\\{" + FdtIds.ToFdtUuid(FdtIds.LibIdFdt100) + "}"))
                {
                    if (tl == null) continue;
                    st.Registered = true;
                    foreach (var v in tl.GetSubKeyNames())
                    {
                        if (!st.Versions.Contains(v)) st.Versions.Add(v);
                        st.Win32Path = st.Win32Path ?? tl.OpenSubKey(v + "\\0\\win32")?.GetValue("") as string;
                        st.Win64Path = st.Win64Path ?? tl.OpenSubKey(v + "\\0\\win64")?.GetValue("") as string;
                    }
                }
            return st;
        }

        // ---------------------------------------------------------------- utilidades

        private static string ViewName(RegistryView v) => v == RegistryView.Registry64 ? "64" : "32";

        private static Guid? TryGuid(string s) => Guid.TryParse(s?.Trim('{', '}'), out var g) ? g : (Guid?)null;

        private static string SubDefault(RegistryKey k, string sub)
        {
            using (var s = k.OpenSubKey(sub)) return s?.GetValue("") as string;
        }

        private static string Clean(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

        private static string StripResourceIndex(string p)
        {
            // "arquivo.dll\2" → recurso TYPELIB de índice 2
            int i = p.LastIndexOf('\\');
            return i > 0 && p.Substring(i + 1).All(char.IsDigit) ? p.Substring(0, i) : p;
        }

        private static string ExtractExe(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return null;
            cmd = cmd.Trim();
            if (cmd.StartsWith("\"")) { int e = cmd.IndexOf('"', 1); return e > 0 ? cmd.Substring(1, e - 1) : cmd.Trim('"'); }
            int x = cmd.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            return x > 0 ? cmd.Substring(0, x + 4) : cmd.Split(' ')[0];
        }

        private static string CodeBaseToPath(string codeBase)
        {
            if (string.IsNullOrEmpty(codeBase)) return null;
            try { return new Uri(codeBase).LocalPath; } catch { return codeBase; }
        }

        /// <summary>
        /// Resolve o caminho como o COM faria para a vista indicada, considerando o redirecionamento
        /// do System32 quando o scanner é x86 e a entrada pertence à vista de 64 bits (e vice-versa).
        /// </summary>
        private static string ResolvePath(string path, RegistryView view)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            path = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));
            string win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            if (!Path.IsPathRooted(path))
            {
                string sysDir = view == RegistryView.Registry64 ? Path.Combine(win, "System32") : Path.Combine(win, Environment.Is64BitOperatingSystem ? "SysWOW64" : "System32");
                path = Path.Combine(sysDir, path);
            }
            string sys32 = Path.Combine(win, "System32") + "\\";
            if (path.StartsWith(sys32, StringComparison.OrdinalIgnoreCase))
            {
                if (view == RegistryView.Registry64 && !Environment.Is64BitProcess)
                    path = Path.Combine(win, "Sysnative", path.Substring(sys32.Length));
                else if (view == RegistryView.Registry32 && Environment.Is64BitProcess)
                    path = Path.Combine(win, "SysWOW64", path.Substring(sys32.Length));
            }
            return path;
        }

        /// <summary>Lê o campo Machine do cabeçalho PE/COFF.</summary>
        public static string ReadPeMachine(string file)
        {
            try
            {
                using (var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var br = new BinaryReader(fs))
                {
                    if (br.ReadUInt16() != 0x5A4D) return null; // "MZ"
                    fs.Position = 0x3C;
                    int peOff = br.ReadInt32();
                    fs.Position = peOff;
                    if (br.ReadUInt32() != 0x00004550) return null; // "PE\0\0"
                    ushort m = br.ReadUInt16();
                    switch (m)
                    {
                        case 0x014C: return "x86";
                        case 0x8664: return "x64";
                        case 0xAA64: return "arm64";
                        default: return "0x" + m.ToString("X4");
                    }
                }
            }
            catch { return null; }
        }
    }
}
