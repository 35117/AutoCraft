@echo off
rem AutoCraft 自动合成插件 一键编译脚本（双击运行）
rem 输出: ..\BepInEx\Plugins\AutoCraftMod.dll

set CSC=C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe

set GAME=%~dp0..

echo [AutoCraft] 正在编译...
"%CSC%" /nologo /t:library /codepage:65001 /out:"%GAME%\BepInEx\Plugins\AutoCraftMod.dll" ^
  /r:"%GAME%\Unturned_Data\Managed\netstandard.dll" ^
  /r:"%GAME%\BepInEx\core\BepInEx.dll" ^
  /r:"%GAME%\BepInEx\core\0Harmony.dll" ^
  /r:"%GAME%\Unturned_Data\Managed\UnityEngine.dll" ^
  /r:"%GAME%\Unturned_Data\Managed\UnityEngine.CoreModule.dll" ^
  /r:"%GAME%\Unturned_Data\Managed\UnityEngine.InputLegacyModule.dll" ^
  /r:"%GAME%\Unturned_Data\Managed\Assembly-CSharp.dll" ^
  /r:"%GAME%\Unturned_Data\Managed\UnturnedDat.dll" ^
  /r:"%GAME%\Unturned_Data\Managed\SDG.Glazier.Runtime.dll" ^
  "%~dp0AutoCraft.cs"

if %ERRORLEVEL% EQU 0 (
  echo.
  echo 编译成功！插件已输出到: %GAME%\BepInEx\Plugins\AutoCraftMod.dll
) else (
  echo.
  echo 编译失败，请检查上面的错误信息。
)
pause
