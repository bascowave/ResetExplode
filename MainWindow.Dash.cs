using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using LibreHardwareMonitor.Hardware;

namespace ResetExplode;

/// <summary>
/// Visão geral: certificados digitais, saúde do PC (segurança), tabela de componentes, anéis de memória/disco,
/// eventos recentes e programas da Receita instalados.
/// </summary>
public partial class MainWindow
{
    public sealed record DashComponent(string Icon, string Name, string Model, string Temp, string Load, string Clock, string Power, string Target);
    public sealed record DashEvent(string Icon, string Title, string Detail, string Value, DateTime At)
    {
        public string When => At.Date == DateTime.Today ? $"hoje {At:HH:mm}" : $"{At:dd/MM HH:mm}";
    }
    public sealed record DashRingItem(Brush Swatch, string Label, string Value);
    public sealed record DashCheck(string Title, string Detail, bool Ok);

    private readonly List<DashEvent> _events = new();
    private List<DiskHealthCard> _certs = new();
    private bool _certAlerted;

    // ---------------- atualização a cada segundo (só com a página aberta) ----------------

    private void CollectDashSample()
    {
        var (total, avail) = Optimizer.MemoryStatus();
        SideTemp.Text = F(_monitor.CpuTemperature, "0.#", " °C");
        SideLoad.Text = F(_monitor.CpuLoad, "0", " %");
        SideRam.Text = total > 0 ? $"{(total - avail) / 1073741824.0:0.0} de {total / 1073741824.0:0} GB" : "—";
        if (!PageDash.IsVisible) return;
        UpdateDashComponents();
        UpdateDashRings();
    }

    // ---------------- eventos recentes ----------------

    private void AddEvent(string icon, string title, string detail, string value)
    {
        _events.Insert(0, new DashEvent(icon, title, detail, value, DateTime.Now));
        if (_events.Count > 30) _events.RemoveAt(_events.Count - 1);
        if (PageDash.IsVisible) ShowEvents();
    }

    private void ShowEvents()
    {
        var list = _events.Take(4).ToList();
        DashEvents.ItemsSource = list;
        DashEventsEmpty.Visibility = list.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---------------- navegação ----------------

    private void DashCerts_Click(object sender, RoutedEventArgs e) => NavCerts.IsChecked = true;
    private void DashSecurity_Click(object sender, RoutedEventArgs e) => NavSecurity.IsChecked = true;
    private void DashFixes_Click(object sender, RoutedEventArgs e) => NavFixes.IsChecked = true;
    private void DashGov_Click(object sender, RoutedEventArgs e) => NavGov.IsChecked = true;

    private void DashGo_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string page }) return;
        var nav = new RadioButton[] { NavSensors, NavGpu, NavSystem, NavDisks }.FirstOrDefault(n => (string)n.Tag == page);
        if (nav != null) nav.IsChecked = true;
    }

    private void UpdateDashboard()
    {
        UpdateDashComponents();
        UpdateDashRings();
        ShowEvents();
        UpdateDashHealth();
    }

    /// <summary>Leituras lentas da Visão geral (certificados, segurança, programas), feitas uma vez na abertura.</summary>
    private async Task LoadDashDataAsync()
    {
        try
        {
            _certs = await Task.Run(CertificateCheck.ReadCertificates);
            ShowDashCerts();
            NotifyCertificates();
        }
        catch (Exception ex) { DashCertSub.Text = "Não foi possível ler os certificados: " + ex.Message; }

        try
        {
            _security = await Task.Run(SecurityCheck.Read);
            UpdateDashHealth();
        }
        catch { }

        try
        {
            var gov = await Task.Run(GovPrograms.Scan);
            var installed = gov.Where(g => g.Installed).ToList();
            DashGovHero.Text = $"{installed.Count} de {gov.Count}";
            DashGov.ItemsSource = gov.Take(6).Select(g => new KeyValuePair<string, string>(g.Name, g.Installed ? "instalado" : "—")).ToList();
        }
        catch { DashGovHero.Text = "—"; }
    }

    private void ShowDashCerts()
    {
        int expired = _certs.Count(c => c.Verdict == DiskVerdict.Critical);
        int soon = _certs.Count(c => c.Verdict == DiskVerdict.Warning);
        DashCertHero.Text = _certs.Count == 0 ? "Nenhum certificado"
                          : expired > 0 ? $"{expired} vencido(s)"
                          : soon > 0 ? $"{soon} vencendo"
                          : "Todos válidos";
        DashCertSub.Text = _certs.Count == 0
            ? "Nenhum certificado ICP-Brasil instalado neste usuário. Para A3, conecte o token."
            : $"{_certs.Count} certificado(s) instalado(s) · alerta {CertificateCheck.WarnDays} dias antes do vencimento";
        DashCerts.ItemsSource = _certs.Take(4).ToList(); // já vem ordenado pelo vencimento mais próximo
    }

    /// <summary>Aviso no canto da tela quando há certificado vencido ou perto de vencer (uma vez por abertura).</summary>
    private void NotifyCertificates()
    {
        if (_certAlerted) return;
        var alert = _certs.Where(c => c.Verdict is DiskVerdict.Critical or DiskVerdict.Warning).ToList();
        if (alert.Count == 0) return;
        _certAlerted = true;
        var first = alert[0];
        string title = first.Verdict == DiskVerdict.Critical ? "Certificado digital vencido" : "Certificado digital vencendo";
        string more = alert.Count > 1 ? $"\n+{alert.Count - 1} outro(s). Veja em Certificados." : "";
        Toast(title, $"{first.Title}\n{first.Reasons.Split('.')[0]}.{more}", "");
    }

    private static string F(float? v, string fmt, string unit) => v is float x ? x.ToString(fmt) + unit : "—";

    /// <summary>Nome curto para a tabela: sem (R), (TM), "CPU", frequência e sufixos de fabricante.</summary>
    private static string ShortName(string name)
    {
        string s = Regex.Replace(name, @"\((R|TM|tm|r)\)|®|™|\bCPU\b|\bProcessor\b|@.*$|\d+-Core|NVIDIA |AMD |Intel |Graphics$", " ");
        return Regex.Replace(s, @"\s+", " ").Trim();
    }

    private void UpdateDashComponents()
    {
        var rows = new List<DashComponent>
        {
            new("", "Processador", ShortName(_cpuName),
                F(_monitor.CpuTemperature, "0", " °C"), F(_monitor.CpuLoad, "0", " %"), F(CpuClockAvg(), "0", " MHz"),
                F(_monitor.CpuPower, "0", " W"), "PageSensors"),
        };
        if (_gpus.Count > 0)
        {
            uint? clk = _gpus[0].NvmlHandle != IntPtr.Zero ? Nvml.Clock(_gpus[0].NvmlHandle, Nvml.ClockGraphics) : null;
            rows.Add(new("", "Placa de vídeo", ShortName(_gpus[0].Name),
                         F(MainGpuTemp(), "0", " °C"), F(MainGpuLoad(), "0", " %"), clk is uint c ? $"{c} MHz" : "—",
                         F(MainGpuPower(), "0", " W"), "PageGpu"));
        }
        var (total, avail) = Optimizer.MemoryStatus();
        if (total > 0)
        {
            double used = total - avail;
            rows.Add(new("", "Memória RAM", $"{total / 1073741824.0:0} GB instalados", "—", $"{used / total:P0}", "—",
                         $"{used / 1073741824.0:0.0} GB", "PageSystem"));
        }
        if (_drives.FirstOrDefault(d => d.Letter.StartsWith("C", StringComparison.OrdinalIgnoreCase)) is { } c0)
            rows.Add(new("", $"Disco {c0.Letter}", c0.Disk.Length > 0 ? c0.Disk : c0.Kind, "—", c0.UsedPercent, "—",
                         $"{DriveCard.Fmt(c0.Free)} livres", "PageDisks"));
        DashComponents.ItemsSource = rows;
    }

    private float? MainGpuLoad() => _gpus.Count == 0 ? null :
        _monitor.Rows.FirstOrDefault(r => r.Hardware == _gpus[0].Name && r.Type == SensorType.Load && r.Name == "GPU Core")?.RawValue;

    private float? CpuClockAvg()
    {
        var c = _monitor.Rows.Where(r => r.HardwareType == HardwareType.Cpu && r.Type == SensorType.Clock &&
                                         r.Name.StartsWith("Core #") && r.RawValue > 0).Select(r => r.RawValue!.Value).ToList();
        return c.Count > 0 ? c.Average() : null;
    }

    // ---------------- anéis concêntricos ----------------

    private void UpdateDashRings()
    {
        var (total, avail) = Optimizer.MemoryStatus();
        double ram = total > 0 ? (double)(total - avail) / total : 0;
        var c0 = _drives.FirstOrDefault(d => d.Letter.StartsWith("C", StringComparison.OrdinalIgnoreCase));

        DashMemHero.Text = total > 0 ? $"{(total - avail) / 1073741824.0:0.0} GB" : "—";
        DashMemSub.Text = total > 0 ? $"de RAM em uso · {total / 1073741824.0:0} GB instalados" : "";

        Brush fg = (Brush)FindResource("Fg"), faint = (Brush)FindResource("Faint");
        var rings = new List<(double Frac, Brush Brush, string Label)> { (ram, fg, "RAM") };
        if (c0 != null) rings.Add((c0.UsedFraction, faint, $"Disco {c0.Letter}"));

        DashRings.Children.Clear();
        double cx = DashRings.Width / 2, cy = DashRings.Height / 2;
        for (int i = 0; i < rings.Count; i++)
        {
            double r = 86 - i * 22;
            DashRings.Children.Add(new Ellipse
            {
                Width = r * 2, Height = r * 2, Stroke = (Brush)FindResource("Line"), StrokeThickness = 12,
                Margin = new Thickness(cx - r, cy - r, 0, 0)
            });
            DashRings.Children.Add(new Path
            {
                Data = RingArc(rings[i].Frac, cx, cy, r), Stroke = rings[i].Brush, StrokeThickness = 12,
                StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round
            });
        }
        var center = new TextBlock { Text = $"{ram:P0}", FontFamily = (FontFamily)FindResource("Mono"), FontSize = 16, FontWeight = FontWeights.SemiBold,
                                     Foreground = fg };
        center.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Canvas.SetLeft(center, cx - center.DesiredSize.Width / 2); Canvas.SetTop(center, cy - center.DesiredSize.Height / 2);
        DashRings.Children.Add(center);

        DashRingLegend.ItemsSource = rings.Select(r => new DashRingItem(r.Brush, r.Label, $"{r.Frac:P0}")).ToList();
    }

    private static Geometry RingArc(double frac, double cx, double cy, double r)
    {
        frac = Math.Clamp(frac, 0, 0.9999);
        if (frac <= 0.001) return Geometry.Empty;
        double a = frac * 2 * Math.PI;
        var fig = new PathFigure { StartPoint = new Point(cx, cy - r) };
        fig.Segments.Add(new ArcSegment(new Point(cx + r * Math.Sin(a), cy - r * Math.Cos(a)), new Size(r, r), 0, frac > 0.5, SweepDirection.Clockwise, true));
        var g = new PathGeometry(new[] { fig });
        g.Freeze();
        return g;
    }

    // ---------------- saúde do PC ----------------

    private void UpdateDashHealth()
    {
        var list = _security.Select(c => new DashCheck(
            c.Title,
            c.Verdict == DiskVerdict.Good ? c.Meta.Length > 0 ? c.Meta : c.Reasons : c.Reasons,
            c.Verdict == DiskVerdict.Good)).ToList();

        if (_monitor.IsOpen) // enquanto detecta o hardware, ainda não dá para saber
        {
            var sensors = _monitor.DiagnoseCpuSensors();
            if (sensors != SensorMonitor.CpuSensorProblem.None)
                list.Add(new DashCheck("Sensores da CPU", SensorMonitor.Explain(sensors).Split('.')[0] + ".", false));
        }
        if (list.Count == 0) list.Add(new DashCheck("Verificando…", "Antivírus, firewall, atualizações e disco.", false));
        DashHealth.ItemsSource = list;
    }
}
