using System.Windows;

namespace ResetExplode;

/// <summary>Páginas Segurança (verificação só leitura) e Correções rápidas.</summary>
public partial class MainWindow
{
    // ================= Segurança =================

    private bool _secBusy;
    private List<DiskHealthCard> _security = new();

    private async void RefreshSecurity()
    {
        if (_secBusy) return;
        _secBusy = true;
        BtnSecScan.IsEnabled = false;
        SecStatus.Text = "Verificando…";
        try
        {
            _security = await Task.Run(SecurityCheck.Read);
            SecCards.ItemsSource = _security;
            int bad = _security.Count(c => c.Verdict is DiskVerdict.Warning or DiskVerdict.Critical);
            SecStatus.Text = (bad == 0 ? "Nenhum problema encontrado" : $"{bad} item(ns) precisa(m) de atenção") + $" · lido às {DateTime.Now:HH:mm:ss}";
            if (PageDash.IsVisible) UpdateDashHealth();
        }
        catch (Exception ex) { SecStatus.Text = "Falhou: " + ex.Message; }
        finally
        {
            _secBusy = false;
            BtnSecScan.IsEnabled = true;
        }
    }

    private void BtnSecScan_Click(object sender, RoutedEventArgs e) => RefreshSecurity();

    // ================= Correções rápidas =================

    private List<QuickFix>? _fixes;

    private void InitFixes()
    {
        if (_fixes != null) return;
        _fixes = QuickFixes.All();
        foreach (var f in _fixes.Where(f => f.NeedsAdmin && !SensorMonitor.IsElevated))
            f.Status = "Precisa do app aberto como administrador.";
        FixList.ItemsSource = _fixes;
    }

    private async void BtnFix_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: QuickFix fix } || fix.Busy) return;
        if (fix.NeedsAdmin && !RequireAdmin()) return;
        if (fix.Id == "explorer" && MessageBox.Show(this, "A barra de tarefas e as pastas abertas vão fechar e abrir de novo. Continuar?",
                                                    "T.I - G.A", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        fix.Busy = true;
        fix.Status = "Executando…";
        try
        {
            fix.Status = await fix.Run(CancellationToken.None);
            AddEvent(fix.Icon, fix.Name, fix.Status.Split('\n')[0], "");
        }
        catch (Exception ex) { fix.Status = "Falhou: " + ((ex as AggregateException)?.InnerException ?? ex).Message; }
        finally { fix.Busy = false; }
    }
}
