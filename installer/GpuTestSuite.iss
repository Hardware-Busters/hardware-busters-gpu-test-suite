#define MyAppName "Hardware Busters GPU Test Suite"
#include "version.iss"
#define MyAppExeName "GpuSuite.App.exe"
#define SourceDir AddBackslash(SourcePath) + "..\release-stage"
#ifnexist "{#SourceDir}\REQUIREMENTS.txt"
  #error The staged public release is missing REQUIREMENTS.txt
#endif
[Setup]
AppId={{7B9DFCEF-E825-4A85-87D4-9B2E8797CC17}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
DefaultDirName={autopf}\Hardware Busters GPU Test Suite
DisableProgramGroupPage=yes
OutputDir=..\artifacts\installer
OutputBaseFilename=HardwareBustersGpuTestSuite-Setup
InfoBeforeFile={#SourceDir}\REQUIREMENTS.txt
Compression=lzma
SolidCompression=yes
[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: recursesubdirs ignoreversion
[Icons]
Name: "{autoprograms}\Hardware Busters GPU Test Suite"; Filename: "{app}\{#MyAppExeName}"
[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Launch Hardware Busters GPU Test Suite"; Flags: nowait postinstall skipifsilent
