using System.Management;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using System.ServiceProcess;
using System.Text.RegularExpressions;

namespace ResetExplode;

/// <summary>
/// Certificados digitais ICP-Brasil (A1 e A3) instalados no Windows e diagnóstico do token/leitora:
/// serviço de cartão inteligente, leitoras conectadas, drivers dos fabricantes e cadeia raiz da ICP-Brasil.
/// Nada aqui acessa a chave privada, então o token nunca pede PIN.
/// </summary>
public static class CertificateCheck
{
    /// <summary>Dias antes do vencimento em que o certificado passa a "ATENÇÃO".</summary>
    public const int WarnDays = 30;

    public static List<DiskHealthCard> ReadCertificates()
    {
        var cards = new List<(DateTime NotAfter, DiskHealthCard Card)>();
        var seen = new HashSet<string>();
        foreach (var loc in new[] { StoreLocation.CurrentUser, StoreLocation.LocalMachine })
        {
            using var store = new X509Store(StoreName.My, loc);
            try { store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly); } catch { continue; }
            foreach (var c in store.Certificates)
            {
                using (c)
                {
                    if (!IsIcpBrasil(c) || !seen.Add(c.Thumbprint)) continue;
                    cards.Add((c.NotAfter, BuildCard(c, loc)));
                }
            }
        }
        return cards.OrderBy(x => x.NotAfter).Select(x => x.Card).ToList();
    }

    private static bool IsIcpBrasil(X509Certificate2 c) =>
        c.Issuer.Contains("ICP-Brasil", StringComparison.OrdinalIgnoreCase)
        || c.Subject.Contains("ICP-Brasil", StringComparison.OrdinalIgnoreCase);

    private static DiskHealthCard BuildCard(X509Certificate2 c, StoreLocation loc)
    {
        string cn = c.GetNameInfo(X509NameType.SimpleName, false);
        // O CN da ICP-Brasil vem como "NOME:CNPJ" (PJ) ou "NOME:CPF" (PF).
        string name = cn, doc = "";
        var m = Regex.Match(cn, @"^(.*?):(\d{11}|\d{14})$");
        if (m.Success) { name = m.Groups[1].Value.Trim(); doc = FormatDoc(m.Groups[2].Value); }

        string provider = KeyProvider(c);
        bool hardware = provider.Contains("Smart Card", StringComparison.OrdinalIgnoreCase)
                        || provider.Contains("eToken", StringComparison.OrdinalIgnoreCase)
                        || provider.Contains("SafeNet", StringComparison.OrdinalIgnoreCase)
                        || provider.Contains("SafeSign", StringComparison.OrdinalIgnoreCase)
                        || provider.Contains("Watchdata", StringComparison.OrdinalIgnoreCase);
        string type = provider == "" ? "sem chave privada" : hardware ? "A3 (token/cartão)" : "A1 (arquivo)";

        int days = (int)Math.Floor((c.NotAfter - DateTime.Now).TotalDays);
        var verdict = days < 0 ? DiskVerdict.Critical : days <= WarnDays ? DiskVerdict.Warning : DiskVerdict.Good;
        string label = days < 0 ? "VENCIDO" : days <= WarnDays ? "VENCE LOGO" : "VÁLIDO";
        string reason = days < 0 ? $"Venceu há {-days} dia(s). Renove com a certificadora e remova o antigo."
                      : days <= WarnDays ? $"Vence em {days} dia(s). Agende a renovação."
                      : $"Válido por mais {days} dia(s).";
        if (provider == "") reason += " Só o certificado público está instalado: não serve para assinar.";

        string issuer = c.GetNameInfo(X509NameType.SimpleName, true);
        return new DiskHealthCard
        {
            Title = name,
            Meta = $"{(doc.Length == 18 ? "CNPJ" : doc.Length > 0 ? "CPF" : "")} {doc}".Trim() + (doc.Length > 0 ? " · " : "") + type,
            Verdict = verdict,
            Label = label,
            Reasons = reason,
            Metrics = new()
            {
                new("Válido de", c.NotBefore.ToString("dd/MM/yyyy")),
                new("Válido até", c.NotAfter.ToString("dd/MM/yyyy HH:mm")),
                new("Emissor", issuer),
                new("Tipo", type),
                new("Armazenamento", provider == "" ? "—" : provider),
                new("Repositório", loc == StoreLocation.CurrentUser ? "Usuário atual" : "Computador"),
                new("Número de série", c.SerialNumber),
            }
        };
    }

    private static string FormatDoc(string d) => d.Length == 14
        ? $"{d[..2]}.{d[2..5]}.{d[5..8]}/{d[8..12]}-{d[12..]}"
        : $"{d[..3]}.{d[3..6]}.{d[6..9]}-{d[9..]}";

    // ---------- Provedor da chave (sem abrir a chave) ----------

    [DllImport("crypt32.dll", SetLastError = true)]
    private static extern bool CertGetCertificateContextProperty(IntPtr ctx, int propId, IntPtr data, ref int size);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CRYPT_KEY_PROV_INFO
    {
        public string pwszContainerName;
        public string pwszProvName;
        public int dwProvType, dwFlags, cProvParam;
        public IntPtr rgProvParam;
        public int dwKeySpec;
    }

    private const int CERT_KEY_PROV_INFO_PROP_ID = 2;

    /// <summary>Nome do provedor da chave privada (ex.: "SafeNet Smart Card Key Storage Provider"), ou "" sem chave.</summary>
    private static string KeyProvider(X509Certificate2 c)
    {
        int size = 0;
        if (!CertGetCertificateContextProperty(c.Handle, CERT_KEY_PROV_INFO_PROP_ID, IntPtr.Zero, ref size) || size == 0) return "";
        IntPtr buf = Marshal.AllocHGlobal(size);
        try
        {
            if (!CertGetCertificateContextProperty(c.Handle, CERT_KEY_PROV_INFO_PROP_ID, buf, ref size)) return "";
            return Marshal.PtrToStructure<CRYPT_KEY_PROV_INFO>(buf).pwszProvName ?? "";
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    // ---------- Diagnóstico do token / leitora ----------

    private static readonly (string Name, string[] Prefixes)[] Middlewares =
    {
        ("SafeNet Authentication Client", new[] { "SafeNet Authentication Client" }),
        ("SafeSign (GD / Starsign)", new[] { "SafeSign", "GD Starsign" }),
        ("Watchdata (ProxKey / CSP)", new[] { "Watchdata", "ProxKey" }),
    };

    public static List<DiskHealthCard> ReadDiagnostics()
    {
        var cards = new List<DiskHealthCard>();

        // Serviço "Cartão Inteligente" (SCardSvr): sem ele nenhum token A3 funciona.
        string svc;
        DiskVerdict sv;
        try
        {
            using var s = new ServiceController("SCardSvr");
            svc = s.Status == ServiceControllerStatus.Running ? "Em execução" : s.Status.ToString();
            sv = s.Status == ServiceControllerStatus.Running ? DiskVerdict.Good : DiskVerdict.Warning;
        }
        catch { svc = "Não encontrado"; sv = DiskVerdict.Critical; }

        // Leitoras e tokens conectados.
        var readers = new List<string>();
        try
        {
            using var q = new ManagementObjectSearcher("SELECT Name, PNPClass, Status FROM Win32_PnPEntity WHERE PNPClass='SmartCardReader' OR PNPClass='SmartCard'");
            foreach (ManagementObject o in q.Get())
                readers.Add($"{o["Name"]}{(o["Status"] as string is "OK" or null ? "" : $" ({o["Status"]})")}");
        }
        catch { }

        cards.Add(new DiskHealthCard
        {
            Title = "Token e leitora",
            Meta = $"{readers.Count} dispositivo(s) conectado(s)",
            Verdict = sv != DiskVerdict.Good ? sv : readers.Count > 0 ? DiskVerdict.Good : DiskVerdict.Unknown,
            Label = sv != DiskVerdict.Good ? "SERVIÇO PARADO" : readers.Count > 0 ? "CONECTADO" : "NENHUM",
            Reasons = sv != DiskVerdict.Good
                ? "O serviço Cartão Inteligente não está rodando: tokens A3 não serão reconhecidos. Use INICIAR SERVIÇO."
                : readers.Count > 0 ? "Leitora/token reconhecido pelo Windows."
                : "Nenhum token ou leitora conectado. Para A3, conecte o token e clique em VERIFICAR.",
            Metrics = new List<KeyValuePair<string, string>> { new("Serviço Cartão Inteligente", svc) }
                .Concat(readers.Select((r, i) => new KeyValuePair<string, string>($"Dispositivo {i + 1}", r))).ToList()
        });

        // Drivers (middleware) dos fabricantes de token.
        var names = AppInstaller.InstalledNames();
        var found = Middlewares.Select(mw => (mw.Name, Installed: names.FirstOrDefault(n => mw.Prefixes.Any(p => n.StartsWith(p, StringComparison.OrdinalIgnoreCase)))))
                               .ToList();
        int nFound = found.Count(f => f.Installed != null);
        cards.Add(new DiskHealthCard
        {
            Title = "Drivers do token",
            Meta = $"{nFound} de {found.Count} instalados",
            Verdict = nFound > 0 ? DiskVerdict.Good : DiskVerdict.Unknown,
            Label = nFound > 0 ? "INSTALADO" : "NENHUM",
            Reasons = nFound > 0 ? "Instale apenas o driver da marca do seu token."
                                 : "Nenhum driver de token encontrado. Para A3, instale o da marca do token (veja no próprio token).",
            Metrics = found.Select(f => new KeyValuePair<string, string>(f.Name, f.Installed ?? "não instalado")).ToList()
        });

        // Cadeia raiz da ICP-Brasil.
        var roots = new List<string>();
        foreach (var loc in new[] { StoreLocation.LocalMachine, StoreLocation.CurrentUser })
        {
            using var store = new X509Store(StoreName.Root, loc);
            try { store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly); } catch { continue; }
            foreach (var c in store.Certificates)
                using (c)
                {
                    string n = c.GetNameInfo(X509NameType.SimpleName, false);
                    if (n.Contains("Raiz Brasileira", StringComparison.OrdinalIgnoreCase) && !roots.Contains(n)) roots.Add(n);
                }
        }
        cards.Add(new DiskHealthCard
        {
            Title = "Cadeia ICP-Brasil",
            Meta = $"{roots.Count} raiz(es) instalada(s)",
            Verdict = roots.Count > 0 ? DiskVerdict.Good : DiskVerdict.Warning,
            Label = roots.Count > 0 ? "OK" : "AUSENTE",
            Reasons = roots.Count > 0 ? "Os certificados raiz da ICP-Brasil estão instalados."
                : "Nenhuma raiz ICP-Brasil instalada: sites do governo podem recusar o certificado. Instale as cadeias pelo site do ITI (gov.br/iti).",
            Metrics = roots.Select(r => new KeyValuePair<string, string>("Raiz", r)).ToList()
        });

        return cards;
    }

    /// <summary>Inicia o serviço Cartão Inteligente (precisa de administrador).</summary>
    public static void StartSmartCardService()
    {
        using var s = new ServiceController("SCardSvr");
        if (s.StartType == ServiceStartMode.Disabled)
            Microsoft.Win32.Registry.SetValue(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\SCardSvr", "Start", 3);
        if (s.Status != ServiceControllerStatus.Running)
        {
            s.Start();
            s.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(15));
        }
    }
}
