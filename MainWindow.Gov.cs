using System.Diagnostics;
using System.Windows;

namespace ResetExplode;

/// <summary>Página Programas da Receita (Receitanet, PGDs e SPED).</summary>
public partial class MainWindow
{
    private bool _govBusy;

    private async void RefreshGov()
    {
        if (_govBusy) return;
        _govBusy = true;
        BtnGovScan.IsEnabled = false;
        GovStatus.Text = "Procurando programas…";
        try
        {
            var list = await Task.Run(GovPrograms.Scan);
            GovList.ItemsSource = list;
            GovStatus.Text = $"{list.Count(p => p.Installed)} de {list.Count} encontrados · lido às {DateTime.Now:HH:mm:ss}";
        }
        catch (Exception ex) { GovStatus.Text = "Falhou: " + ex.Message; }
        finally
        {
            _govBusy = false;
            BtnGovScan.IsEnabled = true;
        }
    }

    private void BtnGovScan_Click(object sender, RoutedEventArgs e) => RefreshGov();

    private void BtnGovOpen_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string url }) return;
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { GovStatus.Text = "Não foi possível abrir o site: " + ex.Message; }
    }
}
