@echo off
rem Builds ServoTester.exe with the .NET Framework C# compiler (no extra packages needed).
cd /d "%~dp0"
"%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /target:winexe /out:ServoTester.exe ^
  /r:System.Windows.Forms.dll /r:System.Drawing.dll ServoTester.cs
