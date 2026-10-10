; SAGE Unlocked setup - TheeHorse 2026
; GPL v3 or later, see LICENSE. https://github.com/TheeHorse/SAGE-Unlocked
;
; Inno Setup script (https://jrsoftware.org/isinfo.php). Same job as the old Setup.cs:
; find the games, put the drop-in files (d3d9.dll or dinput8.dll, CnCFpsUnlocker.dll,
; SAGEUnlocked.ini) next to each game's real exe. Build with:
;   ISCC.exe /DFiles=<folder with d3d9.dll, dinput8.dll, CnCFpsUnlocker.dll> setup.iss

#ifndef Files
  #define Files "..\bin"
#endif
#define Version "1.9.7"

[Setup]
AppId=TheeHorse.CnCFpsUnlocker
AppName=SAGE Unlocked
AppVersion={#Version}
AppVerName=SAGE Unlocked {#Version}
AppPublisher=TheeHorse
AppPublisherURL=https://github.com/TheeHorse/SAGE-Unlocked
VersionInfoVersion={#Version}.0.0
VersionInfoDescription=SAGE Unlocked Setup
CreateAppDir=no
Uninstallable=no
DisableProgramGroupPage=yes
DisableReadyPage=yes
PrivilegesRequired=admin
WizardStyle=classic
; no compression: the files are tiny, and packed data makes some scanners think it's obfuscated
Compression=none
OutputDir=.
OutputBaseFilename=SAGE-Unlocked-Setup

[Files]
Source: "{#Files}\d3d9.dll"; Flags: dontcopy
Source: "{#Files}\dinput8.dll"; Flags: dontcopy
Source: "{#Files}\CnCFpsUnlocker.dll"; Flags: dontcopy
Source: "{#Files}\RA3HighFps.exe"; Flags: dontcopy

[Code]
var
  GamePage: TWizardPage;
  GameList: TNewCheckListBox;
  FpsBox, ZoomBox: TNewComboBox;
  ZoomCheck: TNewCheckBox;
  GameNames, GameDirs: array of String;
  Done: String;
  OldLauncher: Boolean;

function GetDC(hWnd: HWND): LongWord; external 'GetDC@user32.dll stdcall';
function ReleaseDC(hWnd: HWND; hdc: LongWord): Integer; external 'ReleaseDC@user32.dll stdcall';
function GetDeviceCaps(hdc: LongWord; index: Integer): Integer; external 'GetDeviceCaps@gdi32.dll stdcall';

function MonitorHz: Integer;
var dc: LongWord;
begin
  dc := GetDC(0);
  Result := GetDeviceCaps(dc, 116);   { VREFRESH }
  ReleaseDC(0, dc);
  if Result < 30 then Result := 60;
end;

function Norm(s: String): String;
begin
  Result := Lowercase(RemoveBackslashUnlessRoot(ExpandFileName(s)));
end;

function LastPos(sub, s: String): Integer;
var i: Integer;
begin
  Result := 0;
  for i := Length(s) - Length(sub) + 1 downto 1 do
    if Copy(s, i, Length(sub)) = sub then begin Result := i; exit; end;
end;

{ ---- ini (SAGEUnlocked.ini, RA3HighFps.ini up to 1.9.4, has no sections, just key=value) ---- }

function IniKeyLine(line, key: String): Boolean;
var t: String;
begin
  t := Trim(Lowercase(line));
  Result := False;
  if Copy(t, 1, Length(key)) <> key then exit;
  t := Trim(Copy(t, Length(key) + 1, MaxInt));
  Result := Copy(t, 1, 1) = '=';
end;

function ReadIni(ini, key: String): String;
var lines: TArrayOfString; i: Integer;
begin
  Result := '';
  if not LoadStringsFromFile(ini, lines) then exit;
  for i := 0 to GetArrayLength(lines) - 1 do
    if IniKeyLine(lines[i], key) then
    begin
      Result := Trim(Copy(lines[i], Pos('=', lines[i]) + 1, MaxInt));
      exit;
    end;
end;

procedure SetIni(ini, key, value: String);
var lines: TArrayOfString; i, n: Integer;
begin
  if not LoadStringsFromFile(ini, lines) then SetArrayLength(lines, 0);
  n := GetArrayLength(lines);
  for i := 0 to n - 1 do
    if IniKeyLine(lines[i], key) then
    begin
      lines[i] := key + '=' + value;
      SaveStringsToFile(ini, lines, False);
      exit;
    end;
  SetArrayLength(lines, n + 1);
  lines[n] := key + '=' + value;
  SaveStringsToFile(ini, lines, False);
end;

{ ---- the game's real exe, from the newest SkuDef's set-exe line ---- }

{ steam writes the language ("German", "English (US)") to the EA key for this folder }
function RegistryLanguage(game: String): String;
var roots: array of String; names: TArrayOfString; i, j: Integer; dir, lang: String; root: Integer;
begin
  Result := '';
  SetArrayLength(roots, 2);
  roots[0] := 'SOFTWARE\Electronic Arts\Electronic Arts';
  roots[1] := 'SOFTWARE\Electronic Arts';
  if IsWin64 then root := HKLM32 else root := HKLM;
  for i := 0 to 1 do
    if RegGetSubkeyNames(root, roots[i], names) then
      for j := 0 to GetArrayLength(names) - 1 do
        if RegQueryStringValue(root, roots[i] + '\' + names[j], 'Install Dir', dir) or
           RegQueryStringValue(root, roots[i] + '\' + names[j], 'InstallPath', dir) then
          if (Norm(dir) = Norm(game)) and RegQueryStringValue(root, roots[i] + '\' + names[j], 'Language', lang) and (lang <> '') then
          begin
            if Pos(' ', lang) > 0 then lang := Copy(lang, 1, Pos(' ', lang) - 1);
            Result := Lowercase(lang);
            exit;
          end;
end;

{ newest *_<lang>_1.<n>.SkuDef, preferring the registry language, then english }
function LatestSkuDef(game: String): String;
var fr: TFindRec; name, pre, lang, want: String; p, ver, best, pass: Integer;
begin
  Result := '';
  want := RegistryLanguage(game);
  for pass := 0 to 2 do
  begin
    best := -1;
    if FindFirst(AddBackslash(game) + '*_1.*.SkuDef', fr) then
    try
      repeat
        name := ChangeFileExt(fr.Name, '');
        p := LastPos('_1.', name);
        if p = 0 then continue;
        ver := StrToIntDef(Copy(name, p + 3, MaxInt), -1);
        pre := Copy(name, 1, p - 1);
        lang := Lowercase(Copy(pre, LastPos('_', pre) + 1, MaxInt));
        if ((pass = 0) and (lang = want)) or ((pass = 1) and (lang = 'english')) or (pass = 2) then
          if ver > best then begin best := ver; Result := AddBackslash(game) + fr.Name; end;
      until not FindNext(fr);
    finally
      FindClose(fr);
    end;
    if Result <> '' then exit;
  end;
end;

{ bfme2: no SkuDefs, the launcher (lotrbfme2.exe, rotwk lotrbfme2ep1.exe) starts game.dat. the BFME All In One
  Launcher puts the whole game in place of the launcher exe (and there may be no game.dat), so a big launcher is the game }
function IsBigExe(path: String): Boolean;
var sz: Integer;
begin
  Result := FileExists(path) and FileSize(path, sz) and (sz > 4000000);
end;

function Bfme2GameExe(dir: String): String;
begin
  dir := AddBackslash(dir);
  if FileExists(dir + 'game.dat') then Result := dir + 'game.dat'
  else if IsBigExe(dir + 'lotrbfme2ep1.exe') then Result := dir + 'lotrbfme2ep1.exe'
  else if IsBigExe(dir + 'lotrbfme2.exe') then Result := dir + 'lotrbfme2.exe'
  else Result := '';
end;

function IsBfme2Folder(dir: String): Boolean;
begin
  Result := (dir <> '') and (FileExists(AddBackslash(dir) + 'lotrbfme2.exe') or FileExists(AddBackslash(dir) + 'lotrbfme2ep1.exe'))
    and (Bfme2GameExe(dir) <> '');
end;

function ExePath(game: String): String;
var lines: TArrayOfString; i: Integer; sku: String;
begin
  Result := '';
  if IsBfme2Folder(game) then begin Result := Bfme2GameExe(game); exit; end;
  sku := LatestSkuDef(game);
  if (sku = '') or not LoadStringsFromFile(sku, lines) then exit;
  for i := 0 to GetArrayLength(lines) - 1 do
    if Lowercase(Copy(lines[i], 1, 8)) = 'set-exe ' then
    begin
      Result := AddBackslash(game) + Trim(Copy(lines[i], 9, MaxInt));
      exit;
    end;
end;

{ the ini whose settings the page starts from: SAGEUnlocked.ini, or the old RA3HighFps.ini if that's all there is }
function IniPath(game: String): String;
var exe, dir: String;
begin
  exe := ExePath(game);
  if exe <> '' then dir := ExtractFilePath(exe) else dir := AddBackslash(game);
  Result := dir + 'SAGEUnlocked.ini';
  if not FileExists(Result) and FileExists(dir + 'RA3HighFps.ini') then Result := dir + 'RA3HighFps.ini';
end;

{ ---- finding games ---- }

function IsGameFolder(dir: String): Boolean;
var fr: TFindRec;
begin
  Result := (dir <> '') and FindFirst(AddBackslash(dir) + '*_1.*.SkuDef', fr);
  if Result then FindClose(fr)
  else Result := IsBfme2Folder(dir);
end;

function Known(dir: String): Boolean;
var i: Integer;
begin
  Result := False;
  for i := 0 to GetArrayLength(GameDirs) - 1 do
    if Norm(GameDirs[i]) = Norm(dir) then begin Result := True; exit; end;
end;

procedure AddGame(name, dir: String);
var n: Integer;
begin
  if Known(dir) then exit;
  n := GetArrayLength(GameDirs);
  SetArrayLength(GameDirs, n + 1);
  SetArrayLength(GameNames, n + 1);
  GameDirs[n] := RemoveBackslashUnlessRoot(dir);
  GameNames[n] := name;
end;

function GameName(dir: String): String;
var leaf: String;
begin
  leaf := ExtractFileName(RemoveBackslashUnlessRoot(dir));
  if IsBfme2Folder(dir) and FileExists(AddBackslash(dir) + 'lotrbfme2ep1.exe') then begin Result := 'BFME2: Rise of the Witch-king (new, not well tested yet)'; exit; end;
  if IsBfme2Folder(dir) then begin Result := 'Battle for Middle-earth II (new, not well tested yet)'; exit; end;
  case Lowercase(leaf) of
    'command and conquer red alert 3': Result := 'Red Alert 3';
    'command and conquer 3 tiberium wars': Result := 'Tiberium Wars';
    'command and conquer 3 - kane''s wrath': Result := 'Kane''s Wrath';
    'command and conquer red alert 3 uprising', 'command and conquer red alert 3 - uprising': Result := 'Red Alert 3 Uprising';
  else
    Result := leaf;
  end;
end;

procedure FindSteamGames;
var steam, line, p: String; libs, lines, folders: TArrayOfString; i, j, n, q: Integer;
begin
  if not RegQueryStringValue(HKCU, 'Software\Valve\Steam', 'SteamPath', steam) then
    steam := ExpandConstant('{commonpf32}\Steam');
  StringChangeEx(steam, '/', '\', True);
  SetArrayLength(libs, 1);
  libs[0] := steam;
  { "path"		"G:\\SteamLibrary" }
  if LoadStringsFromFile(AddBackslash(steam) + 'steamapps\libraryfolders.vdf', lines) then
    for i := 0 to GetArrayLength(lines) - 1 do
    begin
      line := Trim(lines[i]);
      if Lowercase(Copy(line, 1, 6)) <> '"path"' then continue;
      p := Trim(Copy(line, 7, MaxInt));
      if Copy(p, 1, 1) <> '"' then continue;
      p := Copy(p, 2, MaxInt);
      q := Pos('"', p);
      if q = 0 then continue;
      p := Copy(p, 1, q - 1);
      StringChangeEx(p, '\\', '\', True);
      n := GetArrayLength(libs);
      SetArrayLength(libs, n + 1);
      libs[n] := p;
    end;
  SetArrayLength(folders, 5);
  folders[0] := 'Command and Conquer Red Alert 3';
  folders[1] := 'Command and Conquer 3 Tiberium Wars';
  folders[2] := 'Command and Conquer 3 - Kane''s Wrath';
  folders[3] := 'Command and Conquer Red Alert 3 Uprising';
  folders[4] := 'Command and Conquer Red Alert 3 - Uprising';
  for j := 0 to GetArrayLength(folders) - 1 do
    for i := 0 to GetArrayLength(libs) - 1 do
    begin
      p := AddBackslash(libs[i]) + 'steamapps\common\' + folders[j];
      if IsGameFolder(p) then begin AddGame(GameName(p), p); break; end;
    end;
end;

{ ea app / origin / disc }
procedure FindEAGames;
var roots: array of String; names: TArrayOfString; i, j, r: Integer; key, dir, lbl: String; root: Integer;
begin
  SetArrayLength(roots, 3);
  roots[0] := 'SOFTWARE\Electronic Arts\Electronic Arts';
  roots[1] := 'SOFTWARE\Electronic Arts';
  roots[2] := 'SOFTWARE\EA Games';
  for r := 0 to 1 do
  begin
    if r = 0 then begin if IsWin64 then root := HKLM32 else root := HKLM; end
    else begin if not IsWin64 then continue; root := HKLM64; end;
    for i := 0 to GetArrayLength(roots) - 1 do
      if RegGetSubkeyNames(root, roots[i], names) then
        for j := 0 to GetArrayLength(names) - 1 do
        begin
          key := roots[i] + '\' + names[j];
          if not (RegQueryStringValue(root, key, 'Install Dir', dir) or RegQueryStringValue(root, key, 'InstallPath', dir)) then continue;
          if not IsGameFolder(dir) or Known(dir) then continue;
          if IsBfme2Folder(dir) then begin AddGame(GameName(dir), dir); continue; end;   { bfme2 installs register here too }
          if not (RegQueryStringValue(root, key, 'ProductName', lbl) or RegQueryStringValue(root, key, 'DisplayName', lbl)) then lbl := names[j];
          StringChangeEx(lbl, 'Command & Conquer ', '', True);
          AddGame(lbl + ' (EA app)', dir);
        end;
  end;
end;

{ ---- page ---- }

procedure ZoomCheckClick(Sender: TObject);
begin
  ZoomBox.Enabled := ZoomCheck.Checked;
end;

procedure AddFolderClick(Sender: TObject);
var dir: String;
begin
  dir := '';
  if not BrowseForFolder('Pick the game''s install folder (the one with the .SkuDef files, e.g. ...\Red Alert 3, or the BFME2 / RotWK folder)', dir, False) then exit;
  if not IsGameFolder(dir) then
  begin
    MsgBox('That doesn''t look like a supported game folder (no .SkuDef files, or not a BFME2 / RotWK folder).', mbError, MB_OK);
    exit;
  end;
  if Known(dir) then exit;
  AddGame(GameName(dir), dir);
  GameList.AddCheckBox(GameNames[GetArrayLength(GameNames) - 1], '', 0, True, True, False, False, nil);
end;

procedure InitializeWizard;
var i, f, pick, hz: Integer; lbl: TNewStaticText; btn: TNewButton; s, z: String;
begin
  FindSteamGames;
  FindEAGames;

  GamePage := CreateCustomPage(wpWelcome, 'Select Games', 'Which games should the FPS Unlocker be installed for?');

  lbl := TNewStaticText.Create(GamePage);
  lbl.Parent := GamePage.Surface;
  if GetArrayLength(GameDirs) > 0 then lbl.Caption := 'Install for these games:'
  else lbl.Caption := 'No games found automatically. Use "Add game folder..." below.';

  GameList := TNewCheckListBox.Create(GamePage);
  GameList.Parent := GamePage.Surface;
  GameList.SetBounds(0, lbl.Top + lbl.Height + ScaleY(6), GamePage.SurfaceWidth, ScaleY(96));
  for i := 0 to GetArrayLength(GameDirs) - 1 do
    GameList.AddCheckBox(GameNames[i], '', 0, True, True, False, False, nil);   { same order as GameDirs }

  btn := TNewButton.Create(GamePage);
  btn.Parent := GamePage.Surface;
  btn.Caption := 'Add game folder...';
  btn.SetBounds(0, GameList.Top + GameList.Height + ScaleY(6), ScaleX(130), ScaleY(23));
  btn.OnClick := @AddFolderClick;

  lbl := TNewStaticText.Create(GamePage);
  lbl.Parent := GamePage.Surface;
  lbl.Caption := 'Frame rate:';
  lbl.Top := btn.Top + btn.Height + ScaleY(18);

  hz := MonitorHz;
  FpsBox := TNewComboBox.Create(GamePage);
  FpsBox.Parent := GamePage.Surface;
  FpsBox.Style := csDropDownList;
  FpsBox.SetBounds(ScaleX(90), lbl.Top - ScaleY(4), ScaleX(150), ScaleY(23));
  for f := 2 to 16 do
  begin
    s := IntToStr(f * 15) + ' fps';
    if f = 16 then s := s + ' (experimental)';
    FpsBox.Items.Add(s);
  end;
  pick := 0;
  for i := 0 to GetArrayLength(GameDirs) - 1 do
    if pick = 0 then pick := StrToIntDef(ReadIni(IniPath(GameDirs[i]), 'fps'), 0);
  if (pick < 30) or (pick > 240) or (pick mod 15 <> 0) then
  begin
    pick := hz div 15 * 15;
    if pick < 30 then pick := 30;
    if pick > 240 then pick := 240;
  end;
  FpsBox.ItemIndex := pick div 15 - 2;

  lbl := TNewStaticText.Create(GamePage);
  lbl.Parent := GamePage.Surface;
  lbl.Caption := '(your monitor: ' + IntToStr(hz) + ' Hz)';
  lbl.Font.Color := clGrayText;
  lbl.SetBounds(FpsBox.Left + FpsBox.Width + ScaleX(10), FpsBox.Top + ScaleY(4), ScaleX(150), ScaleY(16));

  ZoomCheck := TNewCheckBox.Create(GamePage);
  ZoomCheck.Parent := GamePage.Surface;
  ZoomCheck.Caption := 'Red Alert 3: let the camera zoom out further';
  ZoomCheck.SetBounds(0, FpsBox.Top + FpsBox.Height + ScaleY(12), GamePage.SurfaceWidth - ScaleX(90), ScaleY(17));
  ZoomCheck.OnClick := @ZoomCheckClick;

  ZoomBox := TNewComboBox.Create(GamePage);
  ZoomBox.Parent := GamePage.Surface;
  ZoomBox.Style := csDropDownList;
  ZoomBox.SetBounds(GamePage.SurfaceWidth - ScaleX(80), ZoomCheck.Top - ScaleY(3), ScaleX(80), ScaleY(23));
  ZoomBox.Items.Add('1.25x');
  ZoomBox.Items.Add('1.5x');
  ZoomBox.ItemIndex := 1;
  for i := 0 to GetArrayLength(GameDirs) - 1 do
  begin
    z := ReadIni(IniPath(GameDirs[i]), 'zoom');
    if (z = '1.25') or (z = '1.5') or (z = '1.75') then   { 1.75 isn't offered any more (#10), it shows as 1.5 }
    begin
      ZoomCheck.Checked := True;
      if z = '1.25' then ZoomBox.ItemIndex := 0;
      break;
    end;
  end;
  ZoomBox.Enabled := ZoomCheck.Checked;
end;

{ ---- installing ---- }

{ proxy dll + CnCFpsUnlocker.dll + ini into one exe folder }
function InstallTo(dir, proxy: String; fps: Integer; zoom: String; var err: String): Boolean;
var ini: String; lines: TArrayOfString;
begin
  Result := False;
  if not FileCopy(ExpandConstant('{tmp}\') + proxy, dir + proxy, False) or
     not FileCopy(ExpandConstant('{tmp}\CnCFpsUnlocker.dll'), dir + 'CnCFpsUnlocker.dll', False) then
  begin
    err := 'couldn''t write to ' + dir + ' (is the game running?)';
    exit;
  end;
  { 1.9.5 renamed RA3HighFps.ini / .log: carry the old ini's settings over, drop the old files }
  ini := dir + 'SAGEUnlocked.ini';
  if FileExists(dir + 'RA3HighFps.ini') then
  begin
    if not FileExists(ini) then RenameFile(dir + 'RA3HighFps.ini', ini)
    else DeleteFile(dir + 'RA3HighFps.ini');
  end;
  DeleteFile(dir + 'RA3HighFps.log');
  if not FileExists(ini) then
  begin
    SetArrayLength(lines, 1);
    lines[0] := '; SAGE Unlocked settings (fps: multiple of 15, 30-240; zoom: ra3 only, 1 = off)';
    SaveStringsToFile(ini, lines, False);
  end;
  SetIni(ini, 'fps', IntToStr(fps));
  SetIni(ini, 'zoom', zoom);
  Result := True;
end;

function InstallGame(game: String; fps: Integer; zoom: String; var err: String): Boolean;
var exe, dir, proxy, old, extra: String; i: Integer;
begin
  Result := False;
  exe := ExePath(game);
  if exe = '' then begin err := 'couldn''t find the game''s exe (SkuDef)'; exit; end;
  dir := ExtractFilePath(exe);
  if Lowercase(ExtractFileExt(exe)) = '.game' then proxy := 'd3d9.dll' else proxy := 'dinput8.dll';   { ra3 : tw/kw }
  ExtractTemporaryFile(proxy);
  ExtractTemporaryFile('CnCFpsUnlocker.dll');
  if not InstallTo(dir, proxy, fps, zoom, err) then exit;
  { mods run older tw/kw versions: tw 1.9 and kw 1.2 (1.02) work too, so install there as well }
  for i := 0 to 1 do
  begin
    if i = 0 then extra := AddBackslash(game) + 'RetailExe\1.9\' else extra := AddBackslash(game) + 'RetailExe\1.2\';
    if (Norm(extra) <> Norm(dir)) and (FileExists(extra + 'cnc3game.dat') or FileExists(extra + 'cnc3ep1.dat')) then
      if not InstallTo(extra, proxy, fps, zoom, err) then exit;
  end;
  { v1.6 launcher (game folder, or next to the exe). still set as the steam launch option it patches
    first and blocks the dll, so swap it for one that just starts the game }
  for i := 0 to 1 do
  begin
    if i = 0 then old := AddBackslash(game) + 'RA3HighFps.exe' else old := dir + 'RA3HighFps.exe';
    if FileExists(old) then
    begin
      ExtractTemporaryFile('RA3HighFps.exe');
      if not FileCopy(ExpandConstant('{tmp}\RA3HighFps.exe'), old, False) then
      begin
        err := 'couldn''t replace the old launcher ' + old + ' (is the game running?)';
        exit;
      end;
      OldLauncher := True;
    end;
  end;
  Result := True;
end;

function NextButtonClick(CurPageID: Integer): Boolean;
var i, fps: Integer; zoom, err: String; any: Boolean;
begin
  Result := True;
  if CurPageID <> GamePage.ID then exit;
  any := False;
  for i := 0 to GameList.Items.Count - 1 do
    if GameList.Checked[i] then any := True;
  if not any then
  begin
    MsgBox('Tick at least one game.', mbInformation, MB_OK);
    Result := False;
    exit;
  end;
  fps := (FpsBox.ItemIndex + 2) * 15;
  zoom := '1';
  if ZoomCheck.Checked then
    case ZoomBox.ItemIndex of
      0: zoom := '1.25';
      1: zoom := '1.5';
    end;
  Done := '';
  OldLauncher := False;
  for i := 0 to GameList.Items.Count - 1 do
    if GameList.Checked[i] then
    begin
      if not InstallGame(GameDirs[i], fps, zoom, err) then
      begin
        MsgBox('Couldn''t install for ' + GameList.ItemCaption[i] + ':' + #13#10#13#10 + err, mbError, MB_OK);
        Result := False;
        exit;
      end;
      if Done <> '' then Done := Done + ', ';
      Done := Done + GameList.ItemCaption[i];
    end;
  { one line, the finish page only has room for a few lines }
  Done := 'Installed at ' + IntToStr(fps) + ' fps for ' + Done + '.' + #13#10#13#10 +
          'Just start the games like you normally do. Run this setup again to change the fps.';
  if OldLauncher then
    Done := Done + #13#10#13#10 + 'Used an older version? You can clear the old Launch Options in Steam ' +
            '(right-click the game > Properties). It works either way.';
end;

procedure CurPageChanged(CurPageID: Integer);
begin
  if CurPageID = wpFinished then
  begin
    WizardForm.FinishedLabel.Caption := Done;
    WizardForm.FinishedLabel.Height := WizardForm.FinishedPage.ClientHeight - WizardForm.FinishedLabel.Top;
  end;
end;
