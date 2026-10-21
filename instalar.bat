@echo off
rem ===================================================================
rem  T.I - G.A - instala ou ATUALIZA o app e o agente do Painel da T.I
rem  Deixe este arquivo na mesma pasta do TI-GA.exe e execute como
rem  administrador (botao direito > Executar como administrador).
rem  Para atualizar: troque o TI-GA.exe na pasta e rode de novo.
rem ===================================================================

rem ---- AJUSTE AQUI ----
set "PAINEL=https://ti.guimaraesaranha.com.br"
set "CHAVE=COLE-AQUI-A-AGENT_KEY"
rem Unidade desta maquina (Matriz ou Filial). Pode deixar vazio e marcar depois no painel.
set "UNIDADE=Matriz"
rem Para liberar scripts nesta maquina, troque para: set "SCRIPTS=--scripts"
set "SCRIPTS="
rem ---------------------

set "DESTINO=%ProgramFiles%\TI-GA"

net session >nul 2>&1
if errorlevel 1 (
    echo Execute este arquivo como ADMINISTRADOR ^(botao direito ^> Executar como administrador^).
    pause
    exit /b 1
)
if not exist "%~dp0TI-GA.exe" (
    echo Nao encontrei o TI-GA.exe ao lado deste arquivo.
    pause
    exit /b 1
)

echo Parando a versao anterior ^(se houver^) ...
schtasks /end /tn "TI-GA Agente" >nul 2>&1
taskkill /f /im TI-GA.exe >nul 2>&1
timeout /t 2 /nobreak >nul

echo Copiando para %DESTINO% ...
if not exist "%DESTINO%" mkdir "%DESTINO%"
copy /y "%~dp0TI-GA.exe" "%DESTINO%\TI-GA.exe" >nul
if errorlevel 1 (
    echo FALHOU ao copiar o TI-GA.exe.
    pause
    exit /b 1
)
if exist "%~dp0LEIA-ME.txt" copy /y "%~dp0LEIA-ME.txt" "%DESTINO%\LEIA-ME.txt" >nul

echo Configurando o agente do Painel da T.I ...
start "" /wait "%DESTINO%\TI-GA.exe" --instalar-agente "%PAINEL%" "%CHAVE%" %UNIDADE% %SCRIPTS%
if errorlevel 1 (
    echo FALHOU ao configurar o agente. Veja %ProgramData%\TI-GA\agente.log
    pause
    exit /b 1
)

echo Criando atalho no menu Iniciar ...
powershell -NoProfile -Command "$s = (New-Object -ComObject WScript.Shell).CreateShortcut([Environment]::GetFolderPath('CommonPrograms') + '\T.I - G.A.lnk'); $s.TargetPath = '%DESTINO%\TI-GA.exe'; $s.Save()" >nul 2>&1

echo.
echo Pronto. O agente ja esta enviando ao painel ^(aparece em ate 1 minuto^).
echo Log: %ProgramData%\TI-GA\agente.log
pause
