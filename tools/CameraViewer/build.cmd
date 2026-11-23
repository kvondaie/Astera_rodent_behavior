@echo off
rem Builds CameraViewer.exe using the .NET Framework C# compiler and Bonsai's OpenCV.Net.
cd /d "%~dp0"
"%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /target:winexe /platform:x64 /out:CameraViewer.exe ^
  /r:"%LOCALAPPDATA%\Bonsai\Packages\OpenCV.Net.3.4.2\lib\net462\OpenCV.Net.dll" ^
  /r:System.Windows.Forms.dll /r:System.Drawing.dll ^
  CameraViewer.cs
