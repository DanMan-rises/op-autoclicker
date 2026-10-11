@echo off
"%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /codepage:65001 /optimize /target:winexe /out:"%~dp0OpAutoClicker.exe" /r:System.Windows.Forms.dll /r:System.Drawing.dll "%~dp0OpAutoClicker.cs"
