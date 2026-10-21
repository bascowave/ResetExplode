using System.Globalization;
using System.IO;
using System.Management;
using Microsoft.Win32;

namespace ResetExplode;

/// <summary>
/// Situação de segurança do PC: antivírus, firewall, Windows Update, espaço no disco do sistema e tempo ligado.
/// Só leitura: nada é alterado.
/// </summary>
public static class SecurityCheck
{
    public static List<DiskHealthCard> Read()
    {
        var cards = new List<DiskHealthCard>();
        foreach (var f in new Func<DiskHealthCard>[] { Antivirus, Firewall, WindowsUpdate, SystemDisk, Uptime })
        {
            try { cards.Add(f()); }
            catch (Exception ex)
            {
                cards.Add(new DiskHealthCard { Title = f.Method.Name, Verdict = DiskVerdict.Unknown, Reasons = "Não foi possível verificar: " + ex.Message });
            }
        }
        return cards;
    }

    private static DiskHealthCard Card(string title, DiskVerdict v, string label, string meta, string reason, params (string, string)[] metrics) => new()
    {
        Title = title, Verdict = v, Label = label, Meta = meta, Reasons = reason,
        Metrics = metrics.Select(m => new KeyValuePair<string, string>(m.Item1, m.Item2)).ToList()
    };

    // ---------- Antivírus (Central de Segurança do Windows) ----------

    private static DiskHealthCard Antivirus()
    {
        var products = new List<(string Name, bool On, bool Updated)>();
        using (var q = new ManagementObjectSearcher(@"root\SecurityCenter2", "SELECT displayName, productState FROM AntiVirusProduct"))
            foreach (ManagementObject o in q.Get())
            {
                // productState: byte do meio = ativo (0x10/0x11), byte baixo = definições (0x00 em dia, 0x10 desatualizadas).
                uint state = Convert.ToUInt32(o["productState"]);
                bool on = ((state >> 8) & 0xFF) is 0x10 or 0x11;
                bool updated = (state & 0xFF) == 0x00;
                products.Add(((string)o["displayName"], on, updated));
            }

        var active = products.Where(p => p.On).ToList();
        var metrics = products.Select(p => (p.Name, (p.On ? "ativo" : "desligado") + (p.Updated ? " · em dia" : " · desatualizado"))).ToArray();
        if (active.Count == 0)
            return Card("Antivírus", DiskVerdict.Critical, "DESLIGADO", "nenhum ativo",
                        "Nenhum antivírus ativo. Ligue o Microsoft Defender ou o antivírus da empresa.", metrics);
        if (active.Any(p => !p.Updated))
            return Card("Antivírus", DiskVerdict.Warning, "DESATUALIZADO", string.Join(", ", active.Select(p => p.Name)),
                        "As definições de vírus estão desatualizadas. Abra o antivírus e atualize.", metrics);
        return Card("Antivírus", DiskVerdict.Good, "ATIVO", string.Join(", ", active.Select(p => p.Name)),
                    "Antivírus ativo e com as definições em dia.", metrics);
    }

    // ---------- Firewall do Windows ----------

    private static DiskHealthCard Firewall()
    {
        var profiles = new[] { ("DomainProfile", "Domínio"), ("StandardProfile", "Privada"), ("PublicProfile", "Pública") };
        var states = new List<(string, string)>();
        int off = 0;
        foreach (var (key, name) in profiles)
        {
            // Política de grupo tem prioridade sobre a configuração local.
            object? v = Registry.GetValue($@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\WindowsFirewall\{key}", "EnableFirewall", null)
                     ?? Registry.GetValue($@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\SharedAccess\Parameters\FirewallPolicy\{key}", "EnableFirewall", null);
            bool on = v is int i && i != 0;
            if (!on) off++;
            states.Add((name, on ? "ligado" : "desligado"));
        }

        // Firewall de terceiros (ex.: suíte de antivírus) também conta.
        var third = new List<string>();
        try
        {
            using var q = new ManagementObjectSearcher(@"root\SecurityCenter2", "SELECT displayName, productState FROM FirewallProduct");
            foreach (ManagementObject o in q.Get())
                if (((Convert.ToUInt32(o["productState"]) >> 8) & 0xFF) is 0x10 or 0x11) third.Add((string)o["displayName"]);
        }
        catch { }
        states.AddRange(third.Select(t => ("Outro firewall", t)));

        if (off == 0 || third.Count > 0)
            return Card("Firewall", DiskVerdict.Good, "LIGADO", third.Count > 0 ? string.Join(", ", third) : "Firewall do Windows",
                        "Firewall ativo.", states.ToArray());
        return Card("Firewall", off == 3 ? DiskVerdict.Critical : DiskVerdict.Warning, "DESLIGADO", $"{off} de 3 perfis desligados",
                    "O firewall está desligado em algum perfil de rede. Ligue em Segurança do Windows > Firewall.", states.ToArray());
    }

    // ---------- Windows Update ----------

    private static DiskHealthCard WindowsUpdate()
    {
        DateTime? last = null;
        using (var q = new ManagementObjectSearcher("SELECT InstalledOn FROM Win32_QuickFixEngineering"))
            foreach (ManagementObject o in q.Get())
            {
                string s = (o["InstalledOn"] as string ?? "").Trim();
                if (DateTime.TryParseExact(s, new[] { "M/d/yyyy", "MM/dd/yyyy", "d/M/yyyy", "yyyyMMdd" }, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
                    && d <= DateTime.Now && (last == null || d > last)) last = d;
            }

        bool reboot = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired") != null
                   || Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending") != null;

        int days = last is DateTime l ? (int)(DateTime.Now - l).TotalDays : -1;
        var metrics = new[] { ("Última atualização", last?.ToString("dd/MM/yyyy") ?? "desconhecida"), ("Reinício pendente", reboot ? "sim" : "não") };
        if (reboot)
            return Card("Windows Update", DiskVerdict.Warning, "REINICIAR", "atualização aguardando reinício",
                        "Há atualizações instaladas esperando o PC reiniciar. Reinicie quando possível.", metrics);
        if (days < 0)
            return Card("Windows Update", DiskVerdict.Unknown, "SEM DADOS", "", "Não foi possível ler o histórico de atualizações.", metrics);
        if (days > 45)
            return Card("Windows Update", DiskVerdict.Warning, "ATRASADO", $"última há {days} dias",
                        "Nenhuma atualização há mais de 45 dias. Abra Configurações > Windows Update e verifique.", metrics);
        return Card("Windows Update", DiskVerdict.Good, "EM DIA", $"última há {days} dia(s)", "Atualizações recentes instaladas.", metrics);
    }

    // ---------- Disco do sistema ----------

    private static DiskHealthCard SystemDisk()
    {
        var d = new DriveInfo(Path.GetPathRoot(Environment.SystemDirectory)!);
        double free = (double)d.AvailableFreeSpace / d.TotalSize;
        string meta = $"{DriveCard.Fmt(d.AvailableFreeSpace)} livres de {DriveCard.Fmt(d.TotalSize)}";
        var v = free < 0.08 ? DiskVerdict.Critical : free < 0.15 ? DiskVerdict.Warning : DiskVerdict.Good;
        return Card($"Espaço no disco {d.Name.TrimEnd('\\')}", v, v == DiskVerdict.Good ? "OK" : "POUCO ESPAÇO", meta,
                    v == DiskVerdict.Good ? $"{free:P0} livre." : $"Só {free:P0} livre. Use Correções rápidas > Limpar arquivos temporários.");
    }

    // ---------- Tempo ligado ----------

    private static DiskHealthCard Uptime()
    {
        var up = TimeSpan.FromMilliseconds(Environment.TickCount64);
        string text = up.TotalDays >= 1 ? $"{(int)up.TotalDays} dia(s) e {up.Hours} h" : $"{up.Hours} h {up.Minutes} min";
        return up.TotalDays > 7
            ? Card("Tempo ligado", DiskVerdict.Warning, "REINICIAR", text,
                   "O PC está ligado há mais de uma semana sem reiniciar. Reiniciar resolve lentidão e conclui atualizações.")
            : Card("Tempo ligado", DiskVerdict.Good, "OK", text, "Reiniciado recentemente.");
    }
}
