using System.Diagnostics;
using System.Windows;

namespace ResetExplode;

/// <summary>Página Certificados digitais (ICP-Brasil A1/A3 e diagnóstico do token).</summary>
public partial class MainWindow
{
    private bool _certBusy;

    private async void RefreshCerts()
    {
        if (_certBusy) return;
        _certBusy = true;
        BtnCertScan.IsEnabled = false;
        CertStatus.Text = "Lendo certificados e token…";
        try
        {
            var (certs, diag) = await Task.Run(() => (CertificateCheck.ReadCertificates(), CertificateCheck.ReadDiagnostics()));
            CertCards.ItemsSource = certs;
            CertDiagCards.ItemsSource = diag;
            CertEmpty.Visibility = certs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            BtnCertSvc.Visibility = diag[0].Label == "SERVIÇO PARADO" ? Visibility.Visible : Visibility.Collapsed;

            int expired = certs.Count(c => c.Verdict == DiskVerdict.Critical);
            int soon = certs.Count(c => c.Verdict == DiskVerdict.Warning);
            string summary = $"{certs.Count} certificado(s)";
            if (expired > 0) summary += $" · {expired} vencido(s)";
            if (soon > 0) summary += $" · {soon} vencendo em até {CertificateCheck.WarnDays} dias";
            CertStatus.Text = $"{summary} · lido às {DateTime.Now:HH:mm:ss}";
        }
        catch (Exception ex) { CertStatus.Text = "Falhou: " + ex.Message; }
        finally
        {
            _certBusy = false;
            BtnCertScan.IsEnabled = true;
        }
    }

    private void BtnCertScan_Click(object sender, RoutedEventArgs e) => RefreshCerts();

    private void BtnCertMgr_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo("certmgr.msc") { UseShellExecute = true }); }
        catch (Exception ex) { CertStatus.Text = "Não foi possível abrir o gerenciador: " + ex.Message; }
    }

    private async void BtnCertSvc_Click(object sender, RoutedEventArgs e)
    {
        if (!RequireAdmin()) return;
        BtnCertSvc.IsEnabled = false;
        try
        {
            await Task.Run(CertificateCheck.StartSmartCardService);
            RefreshCerts();
        }
        catch (Exception ex) { CertStatus.Text = "Não foi possível iniciar o serviço: " + ex.Message; }
        finally { BtnCertSvc.IsEnabled = true; }
    }
}
