using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.Win32;

namespace ResetExplode;

/// <summary>Configuração do envio ao Painel da T.I, em %ProgramData%\TI-GA\agente.json (vale para todos os usuários do PC).</summary>
public sealed class AgentConfig
{
    public string Url { get; set; } = "";
    public string Key { get; set; } = "";
    /// <summary>Matriz, Filial... Vazio = o painel descobre pelo IP público.</summary>
    public string Unit { get; set; } = "";

    public static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "TI-GA");
    private static string FilePath => Path.Combine(Folder, "agente.json");
    public bool IsSet => Url.Length > 0 && Key.Length > 0;

    public static AgentConfig Load()
    {
        try { return JsonSerializer.Deserialize<AgentConfig>(File.ReadAllText(FilePath)) ?? new(); }
        catch { return new(); }
    }

    public void Save()
    {
        Directory.CreateDirectory(Folder);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }
}

/// <summary>
/// Monta e envia o relatório da máquina para o Painel da T.I (POST /api/coleta).
/// No modo --agente roda sem janela, a cada minuto; certificados e segurança são relidos a cada 30 min.
/// </summary>
public sealed class PanelAgent : IDisposable
{
    public const string TaskName = "TI-GA Agente";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    private readonly SensorMonitor _monitor;
    private readonly bool _ownsMonitor;
    private List<CertificateCheck.CertSummary> _certs = new();
    private List<DiskHealthCard> _security = new();
    private DateTime _slowAt = DateTime.MinValue;
    private DateTime _softwareAt = DateTime.MinValue;

    public PanelAgent(SensorMonitor? monitor = null)
    {
        _ownsMonitor = monitor == null;
        _monitor = monitor ?? new SensorMonitor();
    }

    /// <summary>Loop do modo --agente (sem janela).</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        if (_ownsMonitor) { try { await _monitor.OpenAsync(); } catch { } }
        while (!ct.IsCancellationRequested)
        {
            var cfg = AgentConfig.Load(); // relê a cada ciclo: mudar a URL/chave não exige reiniciar
            if (cfg.IsSet)
            {
                try { await SendAsync(cfg, ct); }
                catch (Exception ex) when (ex is not OperationCanceledException) { Log("falha no envio: " + ex.Message); }
            }
            try { await Task.Delay(TimeSpan.FromSeconds(60), ct); } catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>Envia um relatório agora. Retorna a unidade em que o painel registrou a máquina.</summary>
    public async Task<string> SendAsync(AgentConfig cfg, CancellationToken ct = default)
    {
        var body = await BuildAsync(cfg, ct);
        using var req = new HttpRequestMessage(HttpMethod.Post, cfg.Url.TrimEnd('/') + "/api/coleta") { Content = JsonContent.Create(body) };
        req.Headers.Add("X-Agent-Key", cfg.Key);
        using var res = await Http.SendAsync(req, ct);
        if (res.StatusCode == HttpStatusCode.Unauthorized) throw new InvalidOperationException("chave recusada pelo painel");
        res.EnsureSuccessStatusCode();
        var answer = await res.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
        if (answer.TryGetProperty("commands", out var cmds) && cmds.ValueKind == JsonValueKind.Array)
            foreach (var c in cmds.EnumerateArray())
                await RunCommandAsync(cfg, c.GetProperty("id").GetInt64(), c.GetProperty("action").GetString() ?? "", ct);
        return answer.TryGetProperty("unit", out var u) ? u.GetString() ?? "" : "";
    }

    // ---------- ações remotas ----------

    /// <summary>Executa uma ação pedida pelo painel (só as da lista) e devolve o resultado.</summary>
    private async Task RunCommandAsync(AgentConfig cfg, long id, string action, CancellationToken ct)
    {
        bool ok = true;
        string msg;
        Log($"ação {id}: {action}");
        try
        {
            switch (action)
            {
                case "restart":
                    Process.Start(new ProcessStartInfo("shutdown.exe",
                        "/r /t 300 /c \"A T.I vai reiniciar este computador em 5 minutos. Salve o que estiver aberto.\"") { CreateNoWindow = true })?.WaitForExit(10000);
                    msg = "Reinício agendado para daqui a 5 minutos (o usuário foi avisado).";
                    break;
                case "inventory":
                    _softwareAt = DateTime.MinValue; // vai junto no próximo envio
                    msg = "A lista de programas será enviada no próximo minuto.";
                    break;
                default:
                    var fix = QuickFixes.All().FirstOrDefault(f => f.Id == action && f.Id is "print" or "net" or "clock" or "temp")
                              ?? throw new InvalidOperationException("ação não permitida neste agente");
                    msg = await fix.Run(ct);
                    break;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ok = false;
            msg = ((ex as AggregateException)?.InnerException ?? ex).Message;
        }

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post,
                $"{cfg.Url.TrimEnd('/')}/api/comandos/{id}/resultado?maquina={Uri.EscapeDataString(MachineId())}")
            { Content = JsonContent.Create(new { ok, message = msg }) };
            req.Headers.Add("X-Agent-Key", cfg.Key);
            using var res = await Http.SendAsync(req, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { Log($"ação {id}: falha ao devolver resultado: {ex.Message}"); }
    }

    private async Task<object> BuildAsync(AgentConfig cfg, CancellationToken ct)
    {
        if (_ownsMonitor) _monitor.Refresh();
        if ((DateTime.Now - _slowAt).TotalMinutes >= 30)
        {
            _slowAt = DateTime.Now;
            try { _certs = await Task.Run(CertificateCheck.ReadSummary, ct); } catch { }
            try { _security = await Task.Run(SecurityCheck.Read, ct); } catch { }
        }

        List<object>? software = null;
        if ((DateTime.Now - _softwareAt).TotalHours >= 6)
        {
            try { software = await Task.Run(InstalledSoftware, ct); _softwareAt = DateTime.Now; } catch { }
        }

        var (total, avail) = Optimizer.MemoryStatus();
        var sys = new DriveInfo(Path.GetPathRoot(Environment.SystemDirectory)!);
        var (gateway, internet, loss) = await PingAsync(ct);

        return new
        {
            machineId = MachineId(),
            hostname = Environment.MachineName,
            unit = cfg.Unit,
            user = LoggedUser(),
            os = OsName(),
            localIp = LocalIp(),
            appVersion = typeof(PanelAgent).Assembly.GetName().Version?.ToString(3),
            cpuName = CpuName(),
            cpuTemp = _monitor.CpuTemperature,
            cpuLoad = _monitor.CpuLoad,
            ramUsedGb = Math.Round((total - avail) / 1073741824.0, 2),
            ramTotalGb = Math.Round(total / 1073741824.0, 2),
            diskFreeGb = Math.Round(sys.AvailableFreeSpace / 1073741824.0, 1),
            diskTotalGb = Math.Round(sys.TotalSize / 1073741824.0, 1),
            pingGatewayMs = gateway,
            pingInternetMs = internet,
            lossPct = loss,
            uptimeHours = Math.Round(Environment.TickCount64 / 3600000.0, 1),
            security = _security.Select(s => new { title = s.Title, label = s.VerdictText, ok = s.Verdict == DiskVerdict.Good }),
            certs = _certs.Select(c => new { name = c.Name, doc = c.Doc, type = c.Type, notAfter = c.NotAfter }),
            anyDeskId = AnyDeskId(),
            software,
        };
    }

    // ---------- inventário ----------

    /// <summary>Programas de "Aplicativos instalados": máquina (64 e 32 bits) e cada usuário logado (HKEY_USERS).</summary>
    private static List<object> InstalledSoftware()
    {
        var seen = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        void Read(RegistryKey? root)
        {
            if (root == null) return;
            foreach (var sub in root.GetSubKeyNames())
            {
                using var k = root.OpenSubKey(sub);
                if (k?.GetValue("DisplayName") is not string name || string.IsNullOrWhiteSpace(name)) continue;
                if (k.GetValue("SystemComponent") is int sc && sc == 1) continue;           // componentes internos do Windows
                if (k.GetValue("ParentKeyName") != null) continue;                           // atualizações de outro programa
                string version = (k.GetValue("DisplayVersion") as string ?? "").Trim();
                seen.TryAdd($"{name.Trim()}|{version}", new { name = name.Trim(), version, publisher = (k.GetValue("Publisher") as string)?.Trim() });
            }
        }
        const string path = @"Software\Microsoft\Windows\CurrentVersion\Uninstall";
        using (var hklm64 = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64)) Read(hklm64.OpenSubKey(path));
        using (var hklm32 = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32)) Read(hklm32.OpenSubKey(path));
        foreach (var sid in Registry.Users.GetSubKeyNames().Where(x => x.StartsWith("S-1-5-21-") && !x.EndsWith("_Classes")))
            Read(Registry.Users.OpenSubKey($@"{sid}\{path}"));
        return seen.Values.ToList();
    }

    /// <summary>ID do AnyDesk (instalado como serviço) em %ProgramData%\AnyDesk\system.conf.</summary>
    private static string? AnyDeskId()
    {
        try
        {
            string conf = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), @"AnyDesk\system.conf");
            if (!File.Exists(conf)) return null;
            foreach (var line in File.ReadLines(conf))
                if (line.StartsWith("ad.anynet.id=")) return line["ad.anynet.id=".Length..].Trim();
        }
        catch { }
        return null;
    }

    // ---------- rede ----------

    /// <summary>4 pings ao roteador e 4 a 1.1.1.1/8.8.8.8. Retorna média em ms (null = sem resposta) e perda % até a internet.</summary>
    private static async Task<(float? Gateway, float? Internet, float Loss)> PingAsync(CancellationToken ct)
    {
        async Task<(float? Avg, int Lost)> Run(IPAddress ip)
        {
            using var p = new Ping();
            var times = new List<long>();
            int lost = 0;
            for (int i = 0; i < 4; i++)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var r = await p.SendPingAsync(ip, 1500);
                    if (r.Status == IPStatus.Success) times.Add(r.RoundtripTime); else lost++;
                }
                catch { lost++; }
            }
            return (times.Count > 0 ? (float)times.Average() : null, lost);
        }

        float? gw = null;
        if (Gateway() is IPAddress g) gw = (await Run(g)).Avg;
        var a = await Run(IPAddress.Parse("1.1.1.1"));
        var b = await Run(IPAddress.Parse("8.8.8.8"));
        float? net = a.Avg ?? b.Avg;
        float loss = Math.Min(a.Lost, b.Lost) * 25f; // perda só conta se os dois destinos perderam
        return (gw, net, loss);
    }

    private static IPAddress? Gateway() =>
        NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .SelectMany(n => n.GetIPProperties().GatewayAddresses)
            .Select(g => g.Address)
            .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !a.Equals(IPAddress.Any));

    private static string? LocalIp()
    {
        try
        {
            using var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            s.Connect("1.1.1.1", 53); // não envia nada: só descobre a interface usada para sair
            return (s.LocalEndPoint as IPEndPoint)?.Address.ToString();
        }
        catch { return null; }
    }

    // ---------- identificação ----------

    /// <summary>MachineGuid do Windows: estável, mesmo trocando o nome do PC.</summary>
    private static string MachineId() =>
        RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64)
            .OpenSubKey(@"SOFTWARE\Microsoft\Cryptography")?.GetValue("MachineGuid") as string ?? Environment.MachineName;

    private static string OsName()
    {
        using var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
        string name = k?.GetValue("ProductName") as string ?? "Windows";
        // A partir do Windows 11 o ProductName continua dizendo "Windows 10": corrige pelo número do build.
        if (Environment.OSVersion.Version.Build >= 22000) name = name.Replace("Windows 10", "Windows 11");
        return $"{name} {k?.GetValue("DisplayVersion")}".Trim();
    }

    private static string? _cpu;
    private static string CpuName() => _cpu ??= Registry.GetValue(@"HKEY_LOCAL_MACHINE\HARDWARE\DESCRIPTION\System\CentralProcessor\0",
                                                                    "ProcessorNameString", "") is string s ? s.Trim() : "";

    /// <summary>Usuário com sessão aberta no console (o agente roda como tarefa agendada, então Environment.UserName pode não ser ele).</summary>
    private static string LoggedUser()
    {
        try
        {
            var explorer = Process.GetProcessesByName("explorer").FirstOrDefault();
            if (explorer != null)
            {
                using var q = new System.Management.ManagementObjectSearcher($"SELECT * FROM Win32_Process WHERE ProcessId={explorer.Id}");
                foreach (System.Management.ManagementObject o in q.Get())
                {
                    var args = new object[2];
                    if (Convert.ToInt32(o.InvokeMethod("GetOwner", args)) == 0) return (string)args[0];
                }
            }
        }
        catch { }
        return Environment.UserName;
    }

    // ---------- tarefa agendada (iniciar com o Windows, sem pedir administrador) ----------

    public static bool TaskInstalled() => Schtasks($"/query /tn \"{TaskName}\"") == 0;

    /// <summary>Cria a tarefa que abre o TI-GA.exe --agente ao ligar o PC, com privilégio máximo (sem UAC). Exige administrador.</summary>
    public static void InstallTask()
    {
        string exe = Environment.ProcessPath ?? throw new InvalidOperationException("Caminho do executável desconhecido.");
        int code = Schtasks($"/create /f /tn \"{TaskName}\" /sc onstart /ru SYSTEM /rl highest /tr \"\\\"{exe}\\\" --agente\"");
        if (code != 0) throw new InvalidOperationException($"schtasks falhou (código {code}).");
        Schtasks($"/run /tn \"{TaskName}\"");
    }

    public static void RemoveTask()
    {
        Schtasks($"/end /tn \"{TaskName}\"");
        Schtasks($"/delete /f /tn \"{TaskName}\"");
    }

    private static int Schtasks(string args)
    {
        using var p = Process.Start(new ProcessStartInfo("schtasks.exe", args) { CreateNoWindow = true, UseShellExecute = false,
                                                                                   RedirectStandardOutput = true, RedirectStandardError = true })!;
        p.WaitForExit(15000);
        return p.ExitCode;
    }

    public static void Log(string msg)
    {
        try
        {
            Directory.CreateDirectory(AgentConfig.Folder);
            string file = Path.Combine(AgentConfig.Folder, "agente.log");
            if (File.Exists(file) && new FileInfo(file).Length > 512 * 1024) File.Delete(file);
            File.AppendAllText(file, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {msg}{Environment.NewLine}");
        }
        catch { }
    }

    public void Dispose() { if (_ownsMonitor) _monitor.Dispose(); }
}
