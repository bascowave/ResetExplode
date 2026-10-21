@echo off
rem Gera o T.I - G.A pronto para levar a outro PC (nao precisa instalar o .NET la).
rem Saida: ..\TI-GA-dist\TI-GA.exe + LEIA-ME.txt
rem Sempre recompila do zero a partir do codigo atual desta pasta.
cd /d "%~dp0"
echo Fechando o TI-GA se estiver aberto ...
taskkill /f /im TI-GA.exe >nul 2>&1
echo Gerando TI-GA.exe a partir do codigo atual ...
rmdir /s /q obj\Release >nul 2>&1
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=None -o "..\TI-GA-dist" -nologo -v q
if errorlevel 1 (
    echo.
    echo FALHOU. Veja os erros acima. O .exe antigo NAO foi atualizado.
    pause
    exit /b 1
)
copy /y "LEIA-ME.txt" "..\TI-GA-dist\LEIA-ME.txt" >nul
copy /y "instalar.bat" "..\TI-GA-dist\instalar.bat" >nul
echo.
echo Pronto: %~dp0..\TI-GA-dist
for %%F in ("..\TI-GA-dist\TI-GA.exe") do echo Arquivo gerado em %%~tF (confira se e de agora^)
pause
