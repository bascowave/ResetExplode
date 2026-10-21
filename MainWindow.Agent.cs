using System.Windows;

namespace ResetExplode;

/// <summary>Página Painel da T.I: configura o envio ao painel web e liga o agente em segundo plano.</summary>
public partial class MainWindow
{
    private bool _agentInit;

    private void InitAgent()
    {
        if (!_agentInit)
        {
            _agentInit = true;
            var cfg = AgentConfig.Load();
            AgentUrl.Text = cfg.Url;
            AgentKey.Password = cfg.Key;
            AgentUnit.Text = cfg.Unit;
        }
        UpdateAgentTask();
    }

    private void UpdateAgentTask()
    {
        bool on = PanelAgent.TaskInstalled();
        BtnAgentTask.Content = on ? "DESATIVAR SEGUNDO PLANO" : "ATIVAR EM SEGUNDO PLANO";
        if (AgentStatus.Text.Length == 0)
            AgentStatus.Text = on ? "Em segundo plano: ativo (envia mesmo com o app fechado)." : "Em segundo plano: desativado.";
    }

    private async void BtnAgentSave_Click(object sender, RoutedEventArgs e)
    {
        var cfg = new AgentConfig { Url = AgentUrl.Text.Trim(), Key = AgentKey.Password.Trim(), Unit = AgentUnit.Text.Trim() };
        if (!cfg.IsSet) { AgentStatus.Text = "Preencha o endereço e a chave."; return; }
        if (!Uri.TryCreate(cfg.Url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        { AgentStatus.Text = "Endereço inválido. Exemplo: https://ti.guimaraesaranha.com.br"; return; }
        if (uri.Scheme == "http" && !uri.IsLoopback)
            AgentStatus.Text = "Atenção: sem https a chave e os dados viajam sem criptografia. ";
        try { cfg.Save(); }
        catch (Exception ex) { AgentStatus.Text = "Não foi possível salvar (abra como administrador): " + ex.Message; return; }

        AgentStatus.Text = "Salvo. Enviando um teste…";
        try
        {
            using var agent = new PanelAgent(_monitor);
            string unit = await agent.SendAsync(cfg);
            AgentStatus.Text = $"Funcionando: este PC apareceu no painel em \"{unit}\" ({DateTime.Now:HH:mm:ss}).";
        }
        catch (Exception ex) { AgentStatus.Text = "Salvo, mas o teste falhou: " + ((ex as AggregateException)?.InnerException ?? ex).Message; }
    }

    private void BtnAgentTask_Click(object sender, RoutedEventArgs e)
    {
        if (!RequireAdmin()) return;
        try
        {
            if (PanelAgent.TaskInstalled())
            {
                PanelAgent.RemoveTask();
                AgentStatus.Text = "Segundo plano desativado: o envio para quando o app fecha.";
            }
            else
            {
                if (!AgentConfig.Load().IsSet) { AgentStatus.Text = "Salve o endereço e a chave antes."; return; }
                PanelAgent.InstallTask();
                AgentStatus.Text = "Segundo plano ativado: envia a cada minuto e inicia junto com o Windows.";
            }
        }
        catch (Exception ex) { AgentStatus.Text = "Falhou: " + ex.Message; }
        UpdateAgentTask();
    }
}
