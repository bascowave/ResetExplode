using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace ResetExplode;

/// <summary>
/// Executa os scripts PowerShell enviados pelo Painel da T.I. Só roda o que vier assinado pela chave do painel
/// (guardada no agente na primeira conexão), com limite de 5 minutos e saída limitada.
/// "system": roda no próprio agente (SYSTEM). "user": roda na sessão do usuário logado, com o perfil dele.
/// </summary>
public static class ScriptRunner
{
    public static readonly TimeSpan Timeout = TimeSpan.FromMinutes(5);
    private const int MaxOutput = 65536;

    public sealed record Result(int ExitCode, string Output, bool TimedOut);

    /// <summary>Mesmo texto que o painel assina (ScriptSigner.Payload).</summary>
    public static bool Verify(string publicKeyPem, long runId, string machine, string runAs, string body, string signature)
    {
        try
        {
            using var rsa = RSA.Create();
            rsa.ImportFromPem(publicKeyPem);
            return rsa.VerifyData(Encoding.UTF8.GetBytes($"{runId}\n{machine}\n{runAs}\n{body}"), Convert.FromBase64String(signature),
                                  HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        }
        catch { return false; }
    }

    public static Task<Result> RunAsync(string runAs, string body, CancellationToken ct) =>
        runAs == "user" ? Task.Run(() => RunAsUser(body), ct) : RunAsSystemAsync(body, ct);

    /// <summary>
    /// Envolve o script para juntar toda a saída (inclusive erros) e devolver um código de saída coerente:
    /// o $LASTEXITCODE do último programa chamado, ou 1 se houve erro no PowerShell.
    /// </summary>
    private static string Wrap(string body, string? outFile)
    {
        string sink = outFile == null ? "Out-String -Width 200" : $"Out-File -FilePath '{outFile.Replace("'", "''")}' -Encoding utf8 -Width 200";
        return "$ErrorActionPreference = 'Continue'\n$ProgressPreference = 'SilentlyContinue'\n$global:LASTEXITCODE = 0\n" +
               "$__erro = $false\n" +
               $"& {{\n{body}\n}} *>&1 | ForEach-Object {{ if ($_ -is [System.Management.Automation.ErrorRecord]) {{ $__erro = $true }}; $_ }} | {sink}\n" +
               "if ($global:LASTEXITCODE -ne 0) { exit $global:LASTEXITCODE } elseif ($__erro) { exit 1 } else { exit 0 }\n";
    }

    private static string Encode(string script) => Convert.ToBase64String(Encoding.Unicode.GetBytes(script));

    private static string PowerShell =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe");

    private static string Cut(string s) => s.Length > MaxOutput ? s[..MaxOutput] + "\n… (saída cortada)" : s;

    // ---------- SYSTEM ----------

    private static async Task<Result> RunAsSystemAsync(string body, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(PowerShell, $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand {Encode(Wrap(body, null))}")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Windows),
        };
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("Não foi possível iniciar o PowerShell.");
        var outTask = p.StandardOutput.ReadToEndAsync(ct);
        var errTask = p.StandardError.ReadToEndAsync(ct);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(Timeout);
        bool timedOut = false;
        try { await p.WaitForExitAsync(cts.Token); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { timedOut = true; try { p.Kill(true); } catch { } }
        string output = (await outTask) + (await errTask);
        return new Result(timedOut ? -1 : p.ExitCode, Cut(output.Trim()), timedOut);
    }

    // ---------- usuário logado ----------

    private static Result RunAsUser(string body)
    {
        uint session = WTSGetActiveConsoleSessionId();
        if (session == 0xFFFFFFFF || !WTSQueryUserToken(session, out var userToken))
            return new Result(-1, "Nenhum usuário logado neste computador (o script precisa de alguém com sessão aberta).", false);

        IntPtr primary = IntPtr.Zero, env = IntPtr.Zero;
        string? outFile = null;
        try
        {
            if (!DuplicateTokenEx(userToken, 0x02000000 /* MAXIMUM_ALLOWED */, IntPtr.Zero, 2 /* SecurityImpersonation */, 1 /* TokenPrimary */, out primary))
                throw new Win32Exception();

            // Arquivo de saída criado pelo agente, com permissão de escrita só para este usuário.
            string dir = Path.Combine(AgentConfig.Folder, "saida");
            Directory.CreateDirectory(dir);
            outFile = Path.Combine(dir, Guid.NewGuid().ToString("N") + ".txt");
            File.WriteAllText(outFile, "");
            using (var id = new WindowsIdentity(primary))
            {
                var fi = new FileInfo(outFile);
                var acl = fi.GetAccessControl();
                acl.AddAccessRule(new FileSystemAccessRule(id.User!, FileSystemRights.Modify, AccessControlType.Allow));
                fi.SetAccessControl(acl);
            }

            if (!CreateEnvironmentBlock(out env, primary, false)) env = IntPtr.Zero;
            var si = new STARTUPINFO { cb = Marshal.SizeOf<STARTUPINFO>(), lpDesktop = @"winsta0\default" };
            string cmd = $"\"{PowerShell}\" -NoProfile -NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass -EncodedCommand {Encode(Wrap(body, outFile))}";
            const uint CREATE_NO_WINDOW = 0x08000000, CREATE_UNICODE_ENVIRONMENT = 0x00000400;
            if (!CreateProcessAsUser(primary, null, new StringBuilder(cmd), IntPtr.Zero, IntPtr.Zero, false,
                                     CREATE_NO_WINDOW | CREATE_UNICODE_ENVIRONMENT, env, null, ref si, out var pi))
                throw new Win32Exception();
            try
            {
                bool timedOut = WaitForSingleObject(pi.hProcess, (uint)Timeout.TotalMilliseconds) != 0;
                if (timedOut) TerminateProcess(pi.hProcess, 1);
                GetExitCodeProcess(pi.hProcess, out uint code);
                string output = File.Exists(outFile) ? File.ReadAllText(outFile) : "";
                return new Result(timedOut ? -1 : (int)code, Cut(output.Trim()), timedOut);
            }
            finally { CloseHandle(pi.hProcess); CloseHandle(pi.hThread); }
        }
        finally
        {
            if (env != IntPtr.Zero) DestroyEnvironmentBlock(env);
            if (primary != IntPtr.Zero) CloseHandle(primary);
            CloseHandle(userToken);
            if (outFile != null) try { File.Delete(outFile); } catch { }
        }
    }

    // ---------- Win32 ----------

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb;
        public string? lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION { public IntPtr hProcess, hThread; public int dwProcessId, dwThreadId; }

    [DllImport("kernel32.dll")] private static extern uint WTSGetActiveConsoleSessionId();
    [DllImport("wtsapi32.dll", SetLastError = true)] private static extern bool WTSQueryUserToken(uint session, out IntPtr token);
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool DuplicateTokenEx(IntPtr token, uint access, IntPtr attrs, int level, int type, out IntPtr newToken);
    [DllImport("userenv.dll", SetLastError = true)] private static extern bool CreateEnvironmentBlock(out IntPtr env, IntPtr token, bool inherit);
    [DllImport("userenv.dll", SetLastError = true)] private static extern bool DestroyEnvironmentBlock(IntPtr env);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessAsUser(IntPtr token, string? app, StringBuilder cmd, IntPtr pAttr, IntPtr tAttr, bool inherit,
                                                   uint flags, IntPtr env, string? dir, ref STARTUPINFO si, out PROCESS_INFORMATION pi);
    [DllImport("kernel32.dll")] private static extern uint WaitForSingleObject(IntPtr h, uint ms);
    [DllImport("kernel32.dll")] private static extern bool GetExitCodeProcess(IntPtr h, out uint code);
    [DllImport("kernel32.dll")] private static extern bool TerminateProcess(IntPtr h, uint code);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);
}
