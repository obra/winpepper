# Fresh-Notepad cold-start corruption probe (E6).
# Per run: launch a NEW notepad.exe process with a probe-owned temp file,
# inject the 134-unit production string via SendInput KEYEVENTF_UNICODE
# (8-unit chunks, 14 ms) with ZERO prior key events (no Ctrl+A clear -- the
# file is empty), read back via WM_GETTEXT, then read the TARGET THREAD's
# believed keystate via AttachThreadInput+GetKeyState (post-measurement, so
# attach-heal cannot contaminate the run). Cleanup: WM_SETTEXT "", Ctrl+W
# (probe tab only), WM_CLOSE, wait exit.
param(
  [int]$Runs = 10,
  [switch]$CtrlLaunch,   # batch B: hold real Ctrl across process launch/paint
  [switch]$ExistingTabs, # safe mode: user Notepad may be open; probe-owned tabs only
  [string]$Prime = "none",  # none | arrow (one real Right-arrow tap) | ctrla (Ctrl+A,Delete like old probe)
  [int]$SettleMs = 0,       # extra settle after tab open, before injection
  [ValidateSet("SendInput","WmChar","SmtoChar","EmReplaceSel","WmCharFenced")][string]$SendMode = "SendInput",
  [int]$ChunkUnits = 8,     # E9-control: 32 reproduces the pre-b4af9fc send shape
  [switch]$DoubleInject,    # warm-tab regression: inject twice into the same tab
  [switch]$TypeDuring,      # E9d: real letter key tap after chunk 5 (ordering probe)
  [int]$PeriodMs = 14,      # inter-chunk pacing
  [switch]$ClosedLoop,      # after each chunk, poll WM_GETTEXTLENGTH for backpressure
  [switch]$AttackE3,        # post phantom Ctrl to target: DOWN after chunk 1, UP after chunk 6
  [string]$TestText = ""
)
$ErrorActionPreference = "Stop"

Add-Type -Name Native -Namespace Probe -MemberDefinition @'
[StructLayout(LayoutKind.Explicit, Size=40)]
public struct INPUT {
  [FieldOffset(0)]  public int    type;
  [FieldOffset(8)]  public ushort wVk;
  [FieldOffset(10)] public ushort wScan;
  [FieldOffset(12)] public uint   dwFlags;
  [FieldOffset(16)] public uint   time;
  [FieldOffset(24)] public IntPtr dwExtraInfo;
}
[StructLayout(LayoutKind.Sequential)]
public struct GUITHREADINFO {
  public uint cbSize; public uint flags;
  public IntPtr hwndActive; public IntPtr hwndFocus; public IntPtr hwndCapture;
  public IntPtr hwndMenuOwner; public IntPtr hwndMoveSize; public IntPtr hwndCaret;
  public int l; public int t; public int r; public int b;
}
[DllImport("user32.dll", SetLastError=true)]
public static extern uint SendInput(uint n, INPUT[] p, int cb);
[DllImport("user32.dll")] public static extern short GetAsyncKeyState(int vk);
[DllImport("user32.dll")] public static extern short GetKeyState(int vk);
[DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
[DllImport("user32.dll", CharSet=CharSet.Unicode)]
public static extern int GetWindowText(IntPtr h, System.Text.StringBuilder s, int n);
[DllImport("user32.dll")]
public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
[DllImport("user32.dll")]
public static extern bool GetGUIThreadInfo(uint tid, ref GUITHREADINFO info);
[DllImport("user32.dll")]
public static extern bool PostMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
[DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
[DllImport("user32.dll", CharSet=CharSet.Unicode)]
public static extern IntPtr SendMessage(IntPtr h, uint m, IntPtr w, System.Text.StringBuilder s);
[DllImport("user32.dll", CharSet=CharSet.Unicode, EntryPoint="SendMessageW")]
public static extern IntPtr SendMessageStr(IntPtr h, uint m, IntPtr w, string s);
[DllImport("user32.dll")] public static extern bool AttachThreadInput(uint a, uint b, bool attach);
[DllImport("user32.dll", CharSet=CharSet.Unicode, EntryPoint="SendMessageW")]
public static extern IntPtr SendMessagePtr(IntPtr h, uint m, IntPtr w, IntPtr l);
[DllImport("user32.dll", SetLastError=true, EntryPoint="SendMessageTimeoutW")]
public static extern IntPtr SendMessageTimeout(IntPtr h, uint m, IntPtr w, IntPtr l, uint flags, uint timeout, out IntPtr result);
[DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
[DllImport("user32.dll", CharSet=CharSet.Unicode)]
public static extern int GetClassName(IntPtr h, System.Text.StringBuilder s, int n);
'@

$INPUT_SIZE = [Runtime.InteropServices.Marshal]::SizeOf([type][Probe.Native+INPUT])
function New-KeyInput([uint16]$vk, [uint16]$scan, [uint32]$flags) {
  $i = New-Object Probe.Native+INPUT
  $i.type = 1; $i.wVk = $vk; $i.wScan = $scan; $i.dwFlags = $flags
  return $i
}
function Send-UnicodeChunk([string]$chunk) {
  if ($script:SendMode -eq "WmChar") {
    foreach ($ch in $chunk.ToCharArray()) {
      [Probe.Native]::PostMessage($script:target, 0x0102, [IntPtr][int][char]$ch, [IntPtr]1) | Out-Null
    }
    return
  }
  if ($script:SendMode -eq "WmCharFenced") {
    foreach ($ch in $chunk.ToCharArray()) {
      [Probe.Native]::PostMessage($script:target, 0x0102, [IntPtr][int][char]$ch, [IntPtr]1) | Out-Null
    }
    $r = [IntPtr]::Zero   # WM_NULL fence: returns after the posted chunk is processed
    $ok = [Probe.Native]::SendMessageTimeout($script:target, 0x0000, [IntPtr]::Zero, [IntPtr]::Zero, 0x0002, 150, [ref]$r)
    if ($ok -eq [IntPtr]::Zero) { $script:fenceTimeouts++ }
    return
  }
  if ($script:SendMode -eq "SmtoChar") {
    foreach ($ch in $chunk.ToCharArray()) {
      $r = [IntPtr]::Zero
      $ok = [Probe.Native]::SendMessageTimeout($script:target, 0x0102, [IntPtr][int][char]$ch, [IntPtr]1, 0x0002, 150, [ref]$r)
      if ($ok -eq [IntPtr]::Zero) { Write-Output "  SMTO-TIMEOUT/FAIL on '$ch'" }
    }
    return
  }
  if ($script:SendMode -eq "EmReplaceSel") {
    [Probe.Native]::SendMessageStr($script:target, 0x00C2, [IntPtr]1, $chunk) | Out-Null
    return
  }
  $inputs = New-Object 'Probe.Native+INPUT[]' ($chunk.Length * 2)
  $j = 0
  foreach ($ch in $chunk.ToCharArray()) {
    $inputs[$j++] = New-KeyInput 0 ([uint16][char]$ch) 0x0004
    $inputs[$j++] = New-KeyInput 0 ([uint16][char]$ch) 0x0006
  }
  $sent = [Probe.Native]::SendInput($inputs.Count, $inputs, $INPUT_SIZE)
  if ($sent -ne $inputs.Count) { Write-Output "  SENDINPUT-PARTIAL $sent/$($inputs.Count)" }
}
function Send-Vk([uint16]$vk, [bool]$down) {
  $inputs = New-Object 'Probe.Native+INPUT[]' 1
  $inputs[0] = New-KeyInput $vk 0 $(if ($down) { 0 } else { 2 })
  [Probe.Native]::SendInput(1, $inputs, $INPUT_SIZE) | Out-Null
}
function Send-VkTap([uint16]$vk) { Send-Vk $vk $true; Send-Vk $vk $false }
function Get-ForegroundTitle {
  $h = [Probe.Native]::GetForegroundWindow()
  $sb = New-Object System.Text.StringBuilder 512
  [Probe.Native]::GetWindowText($h, $sb, 512) | Out-Null
  return $sb.ToString()
}
function Read-TargetBelief([uint32]$tid) {
  # Attach to the target's input queue and read ITS believed keystate.
  $me = [Probe.Native]::GetCurrentThreadId()
  $ok = [Probe.Native]::AttachThreadInput($me, $tid, $true)
  if (-not $ok) { return "attach-FAILED" }
  try {
    $parts = @()
    foreach ($p in @(@(0x11,'Ctrl'),@(0xA2,'LCtrl'),@(0xA3,'RCtrl'),@(0x10,'Shift'),@(0x12,'Alt'))) {
      $s = [Probe.Native]::GetKeyState([int]$p[0])
      if (($s -band 0x8000) -ne 0) { $parts += "$($p[1])=DOWN" }
    }
    if ($parts.Count -eq 0) { return "all-up" }
    return ($parts -join ",")
  } finally {
    [Probe.Native]::AttachThreadInput($me, $tid, $false) | Out-Null
  }
}

$text = @'
Even though it's been a couple of decades since we worked together, I think back on the lessons that I learned from you all the time. 
'@
$text = $text.TrimEnd() + " "
$chunks = @()
for ($i = 0; $i -lt $text.Length; $i += $ChunkUnits) {
  $chunks += $text.Substring($i, [Math]::Min($ChunkUnits, $text.Length - $i))
}

# Safety: refuse to run if a user Notepad is already open (we must never join
# an existing window or touch user tabs) -- unless -ExistingTabs.
$preIds = @(Get-Process Notepad -ErrorAction SilentlyContinue | ForEach-Object { $_.Id })
if ($preIds.Count -gt 0 -and -not $ExistingTabs) {
  Write-Output "ABORT: a Notepad process is already running. Close it first."
  exit 1
}

$corrupt = 0; $intact = 0; $aborted = 0
for ($run = 1; $run -le $Runs; $run++) {
  $probeFile = Join-Path $env:TEMP ("wp-fresh-{0}-{1}.txt" -f $run, (Get-Random))
  Set-Content -Path $probeFile -Value "" -NoNewline -Encoding Unicode
  $fname = [IO.Path]::GetFileName($probeFile)

  if ($CtrlLaunch) { Send-Vk 0x11 $true }   # real Ctrl held across launch
  Start-Process notepad.exe -ArgumentList "`"$probeFile`""
  # Wait for the window to appear and take foreground naturally.
  $deadline = (Get-Date).AddSeconds(6); $np = $null
  while ((Get-Date) -lt $deadline) {
    Start-Sleep -Milliseconds 250
    $np = Get-Process Notepad -ErrorAction SilentlyContinue |
      Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
    if ($np -and (Get-ForegroundTitle) -match [regex]::Escape($fname)) { break }
    if ($np) { [Probe.Native]::SetForegroundWindow($np.MainWindowHandle) | Out-Null }
  }
  if ($CtrlLaunch) { Start-Sleep -Milliseconds 150; Send-Vk 0x11 $false; Start-Sleep -Milliseconds 300 }

  $title = Get-ForegroundTitle
  if (-not $np -or $title -notmatch [regex]::Escape($fname)) {
    Write-Output "run $run ABORT: foreground='$title' (wanted $fname)"
    $aborted++
    if ($np -and ($preIds -notcontains $np.Id)) { $np | Stop-Process -Force -ErrorAction SilentlyContinue; Start-Sleep -Milliseconds 500 }
    continue
  }

  $fg = [Probe.Native]::GetForegroundWindow()
  $pid2 = 0
  $tid = [Probe.Native]::GetWindowThreadProcessId($fg, [ref]$pid2)
  $gti = New-Object Probe.Native+GUITHREADINFO
  $gti.cbSize = [Runtime.InteropServices.Marshal]::SizeOf([type][Probe.Native+GUITHREADINFO])
  [Probe.Native]::GetGUIThreadInfo($tid, [ref]$gti) | Out-Null
  # E9c: double-sample the focused child 30 ms apart
  Start-Sleep -Milliseconds 30
  $gti2 = New-Object Probe.Native+GUITHREADINFO
  $gti2.cbSize = [Runtime.InteropServices.Marshal]::SizeOf([type][Probe.Native+GUITHREADINFO])
  [Probe.Native]::GetGUIThreadInfo($tid, [ref]$gti2) | Out-Null
  $stable = ($gti.hwndFocus -eq $gti2.hwndFocus) -and ($gti.hwndFocus -ne [IntPtr]::Zero)
  $target = if ($gti2.hwndFocus -ne [IntPtr]::Zero) { $gti2.hwndFocus } else { $fg }
  $script:target = $target
  # E9a: class name + EM_GETSEL gate diagnostics
  $cls = New-Object System.Text.StringBuilder 128
  [Probe.Native]::GetClassName($target, $cls, 128) | Out-Null
  $selR = [IntPtr]::Zero
  $selOk = [Probe.Native]::SendMessageTimeout($target, 0x00B0, [IntPtr]::Zero, [IntPtr]::Zero, 0x0002, 150, [ref]$selR)
  Write-Output ("run $run gate: class=[{0}] stable={1} em_getsel_ok={2} sel=0x{3:X}" -f $cls.ToString(), $stable, ($selOk -ne [IntPtr]::Zero), $selR.ToInt64())
  $script:fenceTimeouts = 0

  if ($SettleMs -gt 0) { Start-Sleep -Milliseconds $SettleMs }
  if ($Prime -eq "arrow") { Send-VkTap 0x27; Start-Sleep -Milliseconds 150 }
  elseif ($Prime -eq "ctrla") {
    Send-Vk 0x11 $true; Send-VkTap 0x41; Send-Vk 0x11 $false
    Start-Sleep -Milliseconds 120; Send-VkTap 0x2E; Start-Sleep -Milliseconds 250
  }
  # INJECT -- zero prior key events into this fresh process (unless primed).
  $swInject = [Diagnostics.Stopwatch]::StartNew()
  $asyncDirty = $false
  $stalls = 0
  $expectedLen = 0
  for ($i = 0; $i -lt $chunks.Count; $i++) {
    if (([Probe.Native]::GetAsyncKeyState(0x11) -band 0x8000) -ne 0) { $asyncDirty = $true }
    Send-UnicodeChunk $chunks[$i]
    $expectedLen += $chunks[$i].Length
    if ($TypeDuring -and $i -eq 5) { Send-VkTap 0x58 }   # real 'X' key
    if ($AttackE3 -and $i -eq 0) { [Probe.Native]::PostMessage($target, 0x100, [IntPtr]0x11, [IntPtr]0x001D0001) | Out-Null }
    if ($AttackE3 -and $i -eq 5) { [Probe.Native]::PostMessage($target, 0x101, [IntPtr]0x11, [IntPtr]([int64]0xC01D0001)) | Out-Null }
    if ($ClosedLoop) {
      # Backpressure: wait until the target's document length reflects this
      # chunk before sending the next (cap 600 ms, then press on).
      $t0 = Get-Date
      while (((Get-Date) - $t0).TotalMilliseconds -lt 600) {
        $len = [Probe.Native]::SendMessagePtr($target, 0x000E, [IntPtr]::Zero, [IntPtr]::Zero).ToInt64()
        if ($len -ge $expectedLen) { break }
        Start-Sleep -Milliseconds 10
      }
      if (((Get-Date) - $t0).TotalMilliseconds -ge 600) { $stalls++ }
    }
    Start-Sleep -Milliseconds $PeriodMs
  }
  if ($ClosedLoop -and $stalls -gt 0) { Write-Output "  CLOSED-LOOP STALLS: $stalls" }
  if ($DoubleInject) {
    Start-Sleep -Milliseconds 300
    for ($i = 0; $i -lt $chunks.Count; $i++) { Send-UnicodeChunk $chunks[$i]; Start-Sleep -Milliseconds $PeriodMs }
  }
  $swInject.Stop()
  if ($script:fenceTimeouts -gt 0) { Write-Output "  FENCE-TIMEOUTS: $($script:fenceTimeouts)" }
  Start-Sleep -Milliseconds 500

  $sb = New-Object System.Text.StringBuilder 4096
  [Probe.Native]::SendMessage($target, 0x000D, [IntPtr]4096, $sb) | Out-Null
  $result = $sb.ToString()

  # Post-measurement belief read (attach may heal -- after capture, harmless).
  $belief = Read-TargetBelief $tid

  $expected = if ($DoubleInject) { $text + $text } else { $text }
  if ($TypeDuring) {
    # ordering probe: PASS if result is expected-with-one-x-inserted; report x position
    $stripped = $result -replace 'x',''
    if ($stripped -ceq $expected -and ($result.Length - $stripped.Length) -eq 1) {
      $xpos = $result.IndexOf('x')
      $intact++; $v = "INTACT-X@$xpos/$($result.Length)"
    } else { $corrupt++; $v = "CORRUPTED" }
  }
  elseif ($result -ceq $expected) { $intact++; $v = "INTACT" } else { $corrupt++; $v = "CORRUPTED" }
  Write-Output "run $run [$v] belief=$belief asyncDirty=$asyncDirty inject_ms=$($swInject.ElapsedMilliseconds)"
  if ($v -eq "CORRUPTED") {
    Write-Output "  EXPECTED: [$text]"
    Write-Output "  RESULT:   [$result]"
  }

  # Cleanup: restore empty content, close our tab, close window, wait exit.
  [Probe.Native]::SendMessageStr($target, 0x000C, [IntPtr]::Zero, "") | Out-Null
  Start-Sleep -Milliseconds 150
  Send-Vk 0x11 $true; Send-VkTap 0x57; Send-Vk 0x11 $false   # Ctrl+W our tab
  Start-Sleep -Milliseconds 400
  $np2 = Get-Process Notepad -ErrorAction SilentlyContinue | Where-Object { $preIds -notcontains $_.Id }
  if ($np2 -and -not $ExistingTabs) {
    [Probe.Native]::PostMessage($np2.MainWindowHandle, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null
    Start-Sleep -Milliseconds 600
    $np3 = Get-Process Notepad -ErrorAction SilentlyContinue
    if ($np3) { Send-VkTap 0x4E; Start-Sleep -Milliseconds 400 }   # 'N' = Don't save if prompted
    $np4 = Get-Process Notepad -ErrorAction SilentlyContinue
    if ($np4) { $np4 | Stop-Process -Force -ErrorAction SilentlyContinue }
  }
  Remove-Item $probeFile -ErrorAction SilentlyContinue
  Start-Sleep -Milliseconds 400
}
Write-Output "SUMMARY runs=$Runs intact=$intact corrupted=$corrupt aborted=$aborted ctrlLaunch=$($CtrlLaunch.IsPresent) prime=$Prime settle=$SettleMs mode=$SendMode period=$PeriodMs closedLoop=$($ClosedLoop.IsPresent)"
