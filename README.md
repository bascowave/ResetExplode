# T.I - G.A — v1.0

Ferramenta de suporte técnico da T.I da **Guimarães Aranha**. Roda nos computadores do escritório e reúne em um lugar
o diagnóstico do PC, os certificados digitais, os programas da Receita e as correções mais comuns, para resolver o
problema na hora ou abrir o chamado já sabendo o que está acontecendo.

> Baseado no [RESET EXPLODE](https://github.com/GustavoReset/ResetExplode), de Gustavo (GustavoReset), licença MIT.
> O aviso de copyright original está em [LICENSE](LICENSE) e deve ser mantido.

## O que o app faz

**Visão geral**
- Certificados digitais instalados, com o vencimento mais próximo em destaque.
- Saúde do PC: antivírus, firewall, Windows Update, espaço no disco e tempo ligado.
- Componentes (processador, placa de vídeo, memória e disco), uso de memória e disco, eventos recentes
  e programas da Receita instalados.
- Aviso no canto da tela ao abrir o app quando há certificado vencido ou vencendo em até 30 dias.

**DIAGNÓSTICO**
- **Sistema**: SO, placa-mãe/BIOS, CPU, RAM (por módulo), GPU e discos via WMI; cartões por unidade (C:, pendrive, rede).
  Botão para **exportar relatório** (HTML ou JSON) com segurança, certificados, discos, rede, programas da Receita e hardware.
- **Sensores**: temperatura, clock, carga, ventoinhas e consumo em tempo real (LibreHardwareMonitorLib).
- **Placa de vídeo**: detalhes do chip, memória, driver e sensores ao vivo.
- **Discos**: saúde SMART/NVMe com veredito (saudável, atenção, crítico).
- **Energia**: bateria (desgaste, ciclos) e plano de energia.
- **Rede**: latência, perda de pacotes, DNS e velocidade, com a indicação de onde está o problema (PC, Wi-Fi ou provedor).
- **Certificados** (`CertificateCheck.cs`): certificados ICP-Brasil A1 e A3 com CNPJ/CPF, emissor e validade;
  diagnóstico do token (serviço Cartão Inteligente, leitoras conectadas, drivers SafeNet/SafeSign/Watchdata
  e cadeia raiz da ICP-Brasil). Não acessa a chave privada, então o token não pede PIN.
- **Segurança** (`SecurityCheck.cs`): antivírus e definições, firewall (inclusive por política de grupo),
  última atualização do Windows e reinício pendente, espaço livre e tempo ligado. Só leitura.

**FERRAMENTAS**
- **Correções rápidas** (`QuickFixes.cs`): destravar a fila da impressora, renovar a conexão de rede,
  acertar o relógio (NTP.br), limpar arquivos temporários, reiniciar o Explorador de Arquivos e testar as unidades de rede.
- **Otimização**: limpa a memória em espera e fecha os programas escolhidos (nada vem marcado).
- **Inicialização**: programas que abrem com o Windows, com desativar, remover e desfazer.
- **Instalar apps**: Chrome, Adobe Reader, 7-Zip, Java 8, AnyDesk, Teams e Lightshot pelo winget (`AppInstaller.cs`).
- **Programas da Receita** (`GovPrograms.cs`): detecta Receitanet, ReceitanetBX, IRPF, GCAP, DCTF, PER/DCOMP,
  SPED (ECD, ECF, EFD ICMS IPI, EFD-Contribuições) e Java, e abre a página oficial de download.
- **Drivers**: inventário e atualização de drivers pela fonte oficial e pelo Windows Update.

## Requisitos
- Windows 10 ou 11, 64 bits.
- O app pede para abrir como administrador (necessário para sensores, fila de impressão e relógio).
- Para temperatura e consumo da CPU: driver PawnIO (`winget install namazso.PawnIO`); o app mostra um botão CORRIGIR quando falta.

## Compilar
```
dotnet build
```
O executável sai em `bin\Debug\net8.0-windows\TI-GA.exe`.

## Gerar o executável para distribuir
Rode `publicar.bat`. Ele gera `..\TI-GA-dist\TI-GA.exe` (arquivo único, com o .NET embutido) e o `LEIA-ME.txt`.
Configurações ficam em `%AppData%\TI-GA` em cada computador.

## Estrutura
| Arquivo | Função |
|---|---|
| `MainWindow.xaml` / `.xaml.cs` | Janela, navegação, sistema, sensores, placa de vídeo, otimização |
| `MainWindow.Dash.cs` | Visão geral |
| `MainWindow.Certs.cs`, `CertificateCheck.cs` | Certificados digitais |
| `MainWindow.Security.cs`, `SecurityCheck.cs`, `QuickFixes.cs` | Segurança e correções rápidas |
| `MainWindow.Gov.cs`, `GovPrograms.cs` | Programas da Receita |
| `MainWindow.Disks.cs`, `DiskHealth.cs`, `ReportBuilder.cs` | Saúde dos discos e relatório exportado |
| `MainWindow.Net.cs`, `NetworkTest.cs`, `BatteryInfo.cs` | Rede, energia e tema |
| `MainWindow.Apps.cs`, `AppInstaller.cs` | Instalar apps |
| `MainWindow.Startup.cs`, `StartupManager.cs` | Inicialização |
| `MainWindow.Driver.cs`, `DriverManager.cs`, `Authenticode.cs` | Drivers (com verificação de assinatura) |

## O que saiu em relação ao RESET EXPLODE
Jogos (biblioteca, FPS estimado, gargalo, overlay), benchmarks, stress test, overclock, RESET MAKER (Arduino/portas COM),
integração Corsair e os ajustes do Windows para jogos.
