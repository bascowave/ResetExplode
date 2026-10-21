using System.IO;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace ResetExplode;

/// <summary>Programa da Receita Federal / SPED da página "Programas da Receita".</summary>
public sealed class GovProgram
{
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    /// <summary>Página oficial de download.</summary>
    public string Url { get; init; } = "";
    /// <summary>Casa com o nome em "Aplicativos instalados".</summary>
    public Regex DisplayName { get; init; } = new("^$");
    /// <summary>Casa com o nome da pasta em C:\Arquivos de Programas RFB (onde a Receita instala a maioria dos PGDs).</summary>
    public Regex? Folder { get; init; }

    public bool Installed { get; set; }
    public string Status { get; set; } = "";
}

/// <summary>
/// Detecta os programas da Receita e do SPED instalados. Eles não estão no winget e mudam de versão todo ano,
/// então a instalação é sempre pela página oficial (botão SITE OFICIAL).
/// </summary>
public static class GovPrograms
{
    private const string Rfb = "https://www.gov.br/receitafederal/pt-br/centrais-de-conteudo/download";
    private const string Sped = "http://sped.rfb.gov.br/";
    private static readonly RegexOptions O = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    public static List<GovProgram> Catalog() => new()
    {
        new() { Name = "Receitanet", Description = "Transmissão das declarações e escriturações para a Receita.",
                Url = $"{Rfb}/receitanet", DisplayName = new(@"^Receitanet(?!\s*BX)", O), Folder = new(@"^Receitanet$", O) },
        new() { Name = "ReceitanetBX", Description = "Download de arquivos da Receita (escriturações SPED, NF-e, recibos).",
                Url = $"{Rfb}/receitanetbx", DisplayName = new(@"^Receitanet\s*BX", O), Folder = new(@"^ReceitanetBX", O) },
        new() { Name = "IRPF", Description = "Declaração do Imposto de Renda Pessoa Física.",
                Url = $"{Rfb}/pgd/dirpf", DisplayName = new(@"^IRPF\s*\d{4}", O), Folder = new(@"^IRPF\d{4}$", O) },
        new() { Name = "GCAP", Description = "Apuração de ganhos de capital (pessoa física).",
                Url = $"{Rfb}/pgd/gcap", DisplayName = new(@"^GCAP", O), Folder = new(@"^GCAP", O) },
        new() { Name = "DCTF Mensal", Description = "Declaração de Débitos e Créditos Tributários Federais.",
                Url = $"{Rfb}/pgd/dctf", DisplayName = new(@"\bDCTF", O), Folder = new(@"^DCTF", O) },
        new() { Name = "PER/DCOMP", Description = "Pedidos de restituição, ressarcimento e compensação.",
                Url = $"{Rfb}/pgd/perdcomp", DisplayName = new(@"PER.?DCOMP", O), Folder = new(@"^PERDCOMP", O) },
        new() { Name = "SPED Contábil (ECD)", Description = "Escrituração Contábil Digital.",
                Url = Sped, DisplayName = new(@"\bECD\b|Cont[áa]bil Digital", O), Folder = new(@"^ECD", O) },
        new() { Name = "SPED ECF", Description = "Escrituração Contábil Fiscal (IRPJ e CSLL).",
                Url = Sped, DisplayName = new(@"\bECF\b|Cont[áa]bil Fiscal", O), Folder = new(@"^ECF", O) },
        new() { Name = "EFD ICMS IPI", Description = "Escrituração Fiscal Digital do ICMS e IPI.",
                Url = Sped, DisplayName = new(@"EFD.?ICMS|Fiscal ICMS", O), Folder = new(@"^(EFD.?)?ICMS", O) },
        new() { Name = "EFD-Contribuições", Description = "Escrituração Fiscal Digital do PIS/Pasep e Cofins.",
                Url = Sped, DisplayName = new(@"EFD.?Contribui", O), Folder = new(@"Contribui", O) },
        new() { Name = "Java (Oracle)", Description = "Alguns programas e assinadores ainda precisam do Java instalado.",
                Url = "https://www.java.com/pt-BR/download/", DisplayName = new(@"^Java(\(TM\))?\s*\d", O) },
    };

    /// <summary>Pastas onde a Receita instala os programas.</summary>
    private static IEnumerable<string> RfbFolders()
    {
        foreach (var root in new[] { @"C:\Arquivos de Programas RFB", @"C:\Arquivos de Programas RFB\Programas SPED" })
        {
            string[] dirs;
            try { dirs = Directory.GetDirectories(root); } catch { continue; }
            foreach (var d in dirs) yield return Path.GetFileName(d);
        }
    }

    /// <summary>(nome, versão) de "Aplicativos instalados" (máquina 64/32 bits e usuário atual).</summary>
    private static List<(string Name, string Version)> InstalledApps()
    {
        var list = new List<(string, string)>();
        const string path = @"Software\Microsoft\Windows\CurrentVersion\Uninstall";
        foreach (var (hive, view) in new[] { (RegistryHive.LocalMachine, RegistryView.Registry64), (RegistryHive.LocalMachine, RegistryView.Registry32),
                                             (RegistryHive.CurrentUser, RegistryView.Default) })
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(hive, view).OpenSubKey(path);
                if (root == null) continue;
                foreach (var sub in root.GetSubKeyNames())
                {
                    using var k = root.OpenSubKey(sub);
                    if (k?.GetValue("DisplayName") is string n && !string.IsNullOrWhiteSpace(n))
                        list.Add((n.Trim(), (k.GetValue("DisplayVersion") as string ?? "").Trim()));
                }
            }
            catch { }
        }
        return list;
    }

    public static List<GovProgram> Scan()
    {
        var apps = InstalledApps();
        var folders = RfbFolders().ToList();
        var catalog = Catalog();
        foreach (var p in catalog)
        {
            var found = apps.Where(a => p.DisplayName.IsMatch(a.Name))
                            .Select(a => a.Version.Length > 0 ? $"{a.Name} ({a.Version})" : a.Name)
                            .Concat(p.Folder == null ? Enumerable.Empty<string>() : folders.Where(f => p.Folder.IsMatch(f)))
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .OrderByDescending(s => s)
                            .ToList();
            p.Installed = found.Count > 0;
            p.Status = p.Installed ? "Encontrado: " + string.Join(" · ", found) : "Não encontrado neste computador.";
        }
        return catalog;
    }
}
