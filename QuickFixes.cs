using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.ServiceProcess;
using System.Text;
using Microsoft.Win32;

namespace ResetExplode;

/// <summary>Uma correção de um clique da página "Correções rápidas".</summary>
public sealed class QuickFix : INotifyPropertyChanged
{
    public string Id { get; init; } = "";
    public string Icon { get; init; } = "";
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public string Button { get; init; } = "EXECUTAR";
    public bool NeedsAdmin { get; init; }
    public Func<CancellationToken, Task<string>> Run { get; init; } = _ => Task.FromResult("");

    private string _status = "";
    private bool _busy;
    public string Status { get => _status; set => Set(ref _status, value); }
    public bool Busy { get => _busy; set { Set(ref _busy, value); OnChanged(nameof(Idle)); } }
    public bool Idle => !Busy;

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged([CallerMemberName] string? p = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
    private void Set<T>(ref T field, T value, [CallerMemberName] string? p = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        OnChanged(p);
    }
}

/// <summary>Correções que resolvem os chamados mais comuns do escritório sem precisar de acesso remoto.</summary>
public static class QuickFixes
{
    public static List<QuickFix> All() => new()
    {
        new() { Id = "print", Icon = "", Name = "Destravar a impressora", NeedsAdmin = true, Button = "LIMPAR FILA",
                Description = "Para o spooler, apaga os documentos presos na fila de impressão e liga de novo. Resolve impressão que não sai ou fica \"Imprimindo\" para sempre.",
                Run = ClearPrintQueue },
        new() { Id = "net", Icon = "", Name = "Renovar a conexão de rede", Button = "RENOVAR",
                Description = "Limpa o cache de DNS e pede um novo endereço IP ao roteador. Resolve site que não abre e \"sem internet\" depois de trocar de rede.",
                Run = RenewNetwork },
        new() { Id = "clock", Icon = "", Name = "Acertar o relógio", NeedsAdmin = true, Button = "SINCRONIZAR",
                Description = "Sincroniza a hora com o servidor oficial. Relógio errado faz o certificado digital falhar no e-CAC e nos sites do governo.",
                Run = SyncClock },
        new() { Id = "temp", Icon = "", Name = "Limpar arquivos temporários", Button = "LIMPAR",
                Description = "Apaga arquivos temporários do usuário e do Windows que não estão em uso. Libera espaço em disco.",
                Run = CleanTemp },
        new() { Id = "explorer", Icon = "", Name = "Reiniciar o Explorador de Arquivos", Button = "REINICIAR",
                Description = "Reinicia a barra de tarefas, o menu Iniciar e as pastas. Resolve barra de tarefas travada ou pastas que não abrem.",
                Run = RestartExplorer },
        new() { Id = "drives", Icon = "", Name = "Testar as unidades de rede", Button = "TESTAR",
                Description = "Confere se as pastas mapeadas do servidor (ex.: Z:) estão acessíveis.",
                Run = TestMappedDrives },
    };

    // ---------- Impressora ----------

    private static async Task<string> ClearPrintQueue(CancellationToken ct)
    {
        string spool = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"spool\PRINTERS");
        int deleted = 0;
        await Task.Run(() =>
        {
            using var s = new ServiceController("Spooler");
            if (s.Status != ServiceControllerStatus.Stopped)
            {
                s.Stop();
                s.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(20));
            }
            foreach (var f in Directory.EnumerateFiles(spool))
                try { File.Delete(f); deleted++; } catch { }
            s.Start();
            s.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(20));
        }, ct);
        return deleted == 0 ? "Fila já estava vazia. Spooler reiniciado." : $"{deleted / 2} documento(s) removido(s) da fila. Spooler reiniciado.";
    }

    // ---------- Rede ----------

    private static async Task<string> RenewNetwork(CancellationToken ct)
    {
        await Exec("ipconfig", "/flushdns", ct);
        var (code, _) = await Exec("ipconfig", "/renew", ct, TimeSpan.FromSeconds(60));
        return code == 0 ? "Cache de DNS limpo e endereço IP renovado." : "Cache de DNS limpo. A renovação do IP falhou (cabo/Wi-Fi conectado?).";
    }

    // ---------- Relógio ----------

    private static async Task<string> SyncClock(CancellationToken ct)
    {
        await Task.Run(() =>
        {
            using var s = new ServiceController("W32Time");
            if (s.Status != ServiceControllerStatus.Running)
            {
                s.Start();
                s.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(15));
            }
        }, ct);
        var (code, output) = await Exec("w32tm", "/resync /force", ct, TimeSpan.FromSeconds(30));
        if (code == 0) return $"Relógio sincronizado: {DateTime.Now:dd/MM/yyyy HH:mm:ss}.";
        // Sem servidor configurado (PC fora do domínio): aponta para o NTP.br e tenta de novo.
        await Exec("w32tm", "/config /manualpeerlist:\"a.st1.ntp.br b.st1.ntp.br pool.ntp.br\" /syncfromflags:manual /update", ct);
        (code, output) = await Exec("w32tm", "/resync /force", ct, TimeSpan.FromSeconds(30));
        return code == 0 ? $"Relógio sincronizado com o NTP.br: {DateTime.Now:dd/MM/yyyy HH:mm:ss}." : "Falhou: " + LastLine(output);
    }

    // ---------- Temporários ----------

    private static Task<string> CleanTemp(CancellationToken ct) => Task.Run(() =>
    {
        long freed = 0;
        int files = 0;
        var cutoff = DateTime.Now.AddHours(-24); // não mexe no que acabou de ser criado (instalações em andamento)
        foreach (var dir in new[] { Path.GetTempPath(), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp") }.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            IEnumerable<string> all;
            try { all = Directory.EnumerateFiles(dir, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }); }
            catch { continue; }
            foreach (var f in all)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var fi = new FileInfo(f);
                    if (fi.LastWriteTime > cutoff) continue;
                    long len = fi.Length;
                    fi.Delete();
                    freed += len; files++;
                }
                catch { } // em uso: fica
            }
            try
            {
                foreach (var d in Directory.EnumerateDirectories(dir, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true })
                                          .OrderByDescending(d => d.Length))
                    try { if (!Directory.EnumerateFileSystemEntries(d).Any()) Directory.Delete(d); } catch { }
            }
            catch { }
        }
        return $"{files} arquivo(s) apagado(s) · {DriveCard.Fmt(freed)} liberados. Arquivos em uso foram mantidos.";
    }, ct);

    // ---------- Explorer ----------

    private static async Task<string> RestartExplorer(CancellationToken ct)
    {
        foreach (var p in Process.GetProcessesByName("explorer"))
            try { p.Kill(); p.WaitForExit(5000); } catch { }
        await Task.Delay(1500, ct);
        // O Windows costuma reabrir sozinho; se não reabrir, abre como usuário comum (não como administrador).
        if (Process.GetProcessesByName("explorer").Length == 0)
            Process.Start(new ProcessStartInfo("explorer.exe") { UseShellExecute = true });
        return "Explorador de Arquivos reiniciado.";
    }

    // ---------- Unidades de rede ----------

    private static Task<string> TestMappedDrives(CancellationToken ct) => Task.Run(() =>
    {
        // Mapeamentos persistentes do usuário (HKCU\Network); vale também com o app aberto como administrador.
        using var net = Registry.CurrentUser.OpenSubKey("Network");
        var letters = net?.GetSubKeyNames() ?? Array.Empty<string>();
        if (letters.Length == 0) return "Nenhuma unidade de rede mapeada para este usuário.";
        var sb = new StringBuilder();
        int bad = 0;
        foreach (var l in letters.OrderBy(x => x))
        {
            ct.ThrowIfCancellationRequested();
            using var k = net!.OpenSubKey(l);
            string path = k?.GetValue("RemotePath") as string ?? "";
            var t = Task.Run(() => Directory.Exists(path));
            bool ok = t.Wait(TimeSpan.FromSeconds(8)) && t.Result;
            if (!ok) bad++;
            sb.Append($"{l.ToUpperInvariant()}: {path} → {(ok ? "OK" : "INACESSÍVEL")}\n");
        }
        sb.Append(bad == 0 ? "Todas acessíveis." : $"{bad} inacessível(is): confira se o servidor está ligado e se este PC está na rede do escritório.");
        return sb.ToString();
    }, ct);

    // ---------- util ----------

    private static async Task<(int Code, string Output)> Exec(string exe, string args, CancellationToken ct, TimeSpan? timeout = null)
    {
        var psi = new ProcessStartInfo(exe, args)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        using var p = Process.Start(psi) ?? throw new InvalidOperationException($"Não foi possível iniciar {exe}.");
        var outTask = p.StandardOutput.ReadToEndAsync(ct);
        var errTask = p.StandardError.ReadToEndAsync(ct);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout ?? TimeSpan.FromSeconds(20));
        try { await p.WaitForExitAsync(cts.Token); }
        catch (OperationCanceledException) { try { p.Kill(true); } catch { } if (ct.IsCancellationRequested) throw; return (-1, "tempo esgotado"); }
        return (p.ExitCode, await outTask + await errTask);
    }

    private static string LastLine(string s) =>
        s.Split('\n', '\r').Select(l => l.Trim()).LastOrDefault(l => l.Length > 0) ?? "";
}
