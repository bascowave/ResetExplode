using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;

namespace ResetExplode;

public partial class App : Application
{
    // Uma única instância: abrir o app de novo só traz a janela que já está aberta para a frente.
    private static Mutex? _single;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindow(string? cls, string title);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr h);

    protected override void OnStartup(StartupEventArgs e)
    {
        // TI-GA.exe --agente: envia os dados ao Painel da T.I, sem janela (iniciado pela tarefa agendada).
        if (e.Args.Contains("--agente", StringComparer.OrdinalIgnoreCase)) { RunAgent(); return; }
        // TI-GA.exe --instalar-agente <url> <chave> [unidade] [--scripts]: configura e ativa o agente (para implantar por GPO/script).
        if (e.Args.Length >= 3 && e.Args[0].Equals("--instalar-agente", StringComparison.OrdinalIgnoreCase))
        {
            int code = 0;
            try
            {
                var rest = e.Args.Skip(3).ToList();
                new AgentConfig
                {
                    Url = e.Args[1], Key = e.Args[2],
                    Unit = rest.FirstOrDefault(x => !x.StartsWith("--")) ?? "",
                    ScriptsEnabled = rest.Contains("--scripts", StringComparer.OrdinalIgnoreCase),
                }.Save();
                PanelAgent.InstallTask();
                PanelAgent.Log("agente instalado por linha de comando");
            }
            catch (Exception ex) { PanelAgent.Log("falha ao instalar: " + ex.Message); code = 1; }
            Shutdown(code);
            return;
        }

        bool first;
        try { _single = new Mutex(true, @"Local\TI-GA.SingleInstance", out first); }
        catch (UnauthorizedAccessException) { first = false; } // criado por outra instância com outro nível de permissão

        if (!first)
        {
            IntPtr h = FindWindow(null, "T.I - G.A");
            if (h != IntPtr.Zero)
            {
                if (IsIconic(h)) ShowWindow(h, 9); // SW_RESTORE
                SetForegroundWindow(h);
            }
            Shutdown();
            return;
        }
        base.OnStartup(e);
        new MainWindow().Show();
    }

    private void RunAgent()
    {
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        // Uma cópia só do agente no PC inteiro (Global: vale entre sessões).
        _single = new Mutex(true, @"Global\TI-GA.Agent", out bool first);
        if (!first) { Shutdown(); return; }
        PanelAgent.Log("agente iniciado");
        var agent = new PanelAgent();
        Task.Run(() => agent.RunAsync(CancellationToken.None));
    }
}
