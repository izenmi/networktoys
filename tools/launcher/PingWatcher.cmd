@echo off
rem ============================================================
rem  PingWatcher（軽量版）の起動用。
rem
rem  軽量版は .NET デスクトップランタイムが別途要る。入っていないと
rem  PingWatcher.exe は Windows 側の起動処理で止まり、英語のダイアログが
rem  出るだけで終わる。アプリのコードは 1 行も動かないので、
rem  「入っていないときの案内」をアプリの中に書くことはできない。
rem
rem  そこで、起動する前にここで確かめて日本語で案内する。
rem  ランタイムが入っていれば、そのまま exe を起動して即座に閉じる。
rem
rem  このファイルは UTF-8 で保存してある。cmd に日本語を素通しさせるため
rem  先頭で chcp 65001 に切り替える（cp932 のままだと引数が化ける）。
rem
rem  ※ 軽量版にだけ同梱する（単一exe版には要らない）。
rem     配置は .github/workflows/build.yml が行う。
rem ============================================================
chcp 65001 >nul
setlocal

set "EXE=%~dp0PingWatcher.exe"

if not exist "%EXE%" (
    powershell -NoProfile -Command "Add-Type -AssemblyName PresentationFramework; [void][System.Windows.MessageBox]::Show('PingWatcher.exe が見つかりません。このファイルは PingWatcher.exe と同じフォルダに置いてください。', 'PingWatcher', 'OK', 'Warning')"
    exit /b 1
)

rem 導入先のフォルダを直接見る。dotnet コマンドは PATH に無いことがある。
rem 32bit 版の cmd から呼ばれても拾えるよう、両方の Program Files を見る
set "FOUND="
if defined ProgramW6432 call :probe "%ProgramW6432%"
if not defined FOUND call :probe "%ProgramFiles%"

if defined FOUND goto run

rem 見つからないとき。案内して、望むなら入手ページを開く。
rem リンク先は Windows 自身が使うのと同じもので、版と CPU に合った
rem インストーラへ誘導してくれる（固定の配布 URL は版が変わると腐る）
set "URL=https://aka.ms/dotnet-core-applaunch?framework=Microsoft.WindowsDesktop.App&framework_version=10.0.0&arch=x64&rid=win-x64"

powershell -NoProfile -Command ^
  "Add-Type -AssemblyName PresentationFramework;" ^
  "$nl = [char]10;" ^
  "$m = '.NET デスクトップランタイム 10 が見つかりませんでした。' + $nl + $nl +" ^
  "     'PingWatcher の軽量版はこれが別途必要です。入手ページを開きますか？' + $nl + $nl +" ^
  "     '・「.NET Desktop Runtime」の x64 版を入れてください' + $nl +" ^
  "     '・入れずに使いたい場合は、ランタイム同梱の単一exe版をお使いください';" ^
  "if ([System.Windows.MessageBox]::Show($m, 'PingWatcher', 'YesNo', 'Information') -eq 'Yes') {" ^
  "  Start-Process '%URL%' }"

exit /b 1

:probe
rem %1 = Program Files のパス。10.x のフォルダがあれば FOUND を立てる
dir /b "%~1\dotnet\shared\Microsoft.WindowsDesktop.App\10.*" >nul 2>&1 && set "FOUND=1"
goto :eof

:run
start "" "%EXE%" %*
exit /b 0
