#define MyAppName "Hardware Busters GPU Test Suite"
#include "version.iss"
#define MyAppExeName "GpuSuite.App.exe"
[Setup]
AppId={{7B9DFCEF-E825-4A85-87D4-9B2E8797CC17}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
DefaultDirName={autopf}\Hardware Busters GPU Test Suite
DisableProgramGroupPage=yes
OutputDir=..\artifacts\installer
OutputBaseFilename=HardwareBustersGpuTestSuite-Setup
Compression=lzma
SolidCompression=yes
[Files]
Source: "..\release-stage\*"; DestDir: "{app}"; Flags: recursesubdirs ignoreversion
[Icons]
Name: "{autoprograms}\Hardware Busters GPU Test Suite"; Filename: "{app}\{#MyAppExeName}"
[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Launch Hardware Busters GPU Test Suite"; Flags: nowait postinstall skipifsilent
