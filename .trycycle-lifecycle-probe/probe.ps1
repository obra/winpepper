# Winpepper injection-corruption probe: E1 / E3
# E1: async-visible Ctrl pulse (SendInput VK_CONTROL) spanning chunks 2-6
# E3: queue-only Ctrl (PostMessage WM_KEYDOWN/WM_KEYUP to Notepad's focused child)
# Replicates TextInjector's exact send shape: 8-unit chunks, down+up
# KEYEVENTF_UNICODE pairs, one SendInput batch per chunk, 14 ms period.
param(
  [Parameter(Mandatory)][ValidateSet("E1","E3")][string]$Experiment,
  [switch]$Prelude,                       # E4a: in-band Ctrl-up clearing per chunk
  [string]$PreludeVks = "0x11,0xA2,0xA3", # E4d: VK set to clear (generic, L, R)
  [switch]$PreludePair,                   # V1: full Ctrl down+up transition per chunk
  [ValidateSet("SendInput","WmChar")][string]$SendMode = "SendInput"  # V2: posted WM_CHAR delivery
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
[DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
'@

$INPUT_SIZE = [Runtime.InteropServices.Marshal]::SizeOf([type][Probe.Native+INPUT])

function New-KeyInput([uint16]$vk, [uint16]$scan, [uint32]$flags) {
  $i = New-Object Probe.Native+INPUT
  $i.type = 1; $i.wVk = $vk; $i.wScan = $scan; $i.dwFlags = $flags
  return $i
}

# Exact TextInjector.BuildKeyDownUpInputs shape: unicode down+up per UTF-16 unit.
# With -Prelude (E4a): Ctrl-class KEYUP events prepended INSIDE the same batch,
# clearing the target's translation context in queue order adjacent to the text.
$preludeVkList = @()
if ($Prelude) { $preludeVkList = $PreludeVks -split "," | ForEach-Object { [uint16]($_.Trim() -as [int]) } }

function Send-UnicodeChunk([string]$chunk) {
  if ($SendMode -eq "WmChar") {
    # V2: post WM_CHAR directly -- carries the final char, no VK_PACKET translation step
    foreach ($ch in $chunk.ToCharArray()) {
      [Probe.Native]::PostMessage($script:target, 0x0102, [IntPtr][int][char]$ch, [IntPtr]1) | Out-Null
    }
    return
  }
  $preludeCount = $preludeVkList.Count + $(if ($PreludePair) { 2 } else { 0 })
  $inputs = New-Object 'Probe.Native+INPUT[]' ($preludeCount + $chunk.Length * 2)
  $j = 0
  if ($PreludePair) {                                                  # V1: full transition
    $inputs[$j++] = New-KeyInput 0x11 0 0x0000                         # Ctrl down
    $inputs[$j++] = New-KeyInput 0x11 0 0x0002                         # Ctrl up
  }
  foreach ($vk in $preludeVkList) {
    $inputs[$j++] = New-KeyInput $vk 0 0x0002                          # KEYEVENTF_KEYUP
  }
  foreach ($ch in $chunk.ToCharArray()) {
    $inputs[$j++] = New-KeyInput 0 ([uint16][char]$ch) 0x0004          # KEYEVENTF_UNICODE
    $inputs[$j++] = New-KeyInput 0 ([uint16][char]$ch) 0x0006          # | KEYEVENTF_KEYUP
  }
  $sent = [Probe.Native]::SendInput($inputs.Count, $inputs, $INPUT_SIZE)
  if ($sent -ne $inputs.Count) { Write-Output "SENDINPUT-PARTIAL $sent/$($inputs.Count)" }
}

function Send-VkTap([uint16]$vk) {   # single vk down+up in one batch
  $inputs = New-Object 'Probe.Native+INPUT[]' 2
  $inputs[0] = New-KeyInput $vk 0 0
  $inputs[1] = New-KeyInput $vk 0 2
  [Probe.Native]::SendInput(2, $inputs, $INPUT_SIZE) | Out-Null
}
function Send-Vk([uint16]$vk, [bool]$down) {
  $inputs = New-Object 'Probe.Native+INPUT[]' 1
  $inputs[0] = New-KeyInput $vk 0 $(if ($down) { 0 } else { 2 })
  [Probe.Native]::SendInput(1, $inputs, $INPUT_SIZE) | Out-Null
}

function Get-ForegroundTitle {
  $h = [Probe.Native]::GetForegroundWindow()
  $sb = New-Object System.Text.StringBuilder 256
  [Probe.Native]::GetWindowText($h, $sb, 256) | Out-Null
  return $sb.ToString()
}

# --- Setup: launch + focus Notepad -------------------------------------------
# Open a probe-owned temp file so we NEVER type into (or clear) a user tab.
$probeFile = Join-Path $env:TEMP ("winpepper-probe-{0}.txt" -f (Get-Random))
Set-Content -Path $probeFile -Value "" -NoNewline -Encoding Unicode
Start-Process notepad.exe -ArgumentList "`"$probeFile`""
Start-Sleep -Milliseconds 1500
$np = Get-Process Notepad -ErrorAction SilentlyContinue |
  Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
if (-not $np) { Write-Output "ABORT: no Notepad window found"; exit 1 }
$shell = New-Object -ComObject WScript.Shell
$activated = $false
for ($try = 1; $try -le 6 -and -not $activated; $try++) {
  $shell.AppActivate($np.Id) | Out-Null
  Start-Sleep -Milliseconds 250
  if ((Get-ForegroundTitle) -match "Notepad") { $activated = $true; break }
  [Probe.Native]::SetForegroundWindow($np.MainWindowHandle) | Out-Null
  Start-Sleep -Milliseconds 250
  if ((Get-ForegroundTitle) -match "Notepad") { $activated = $true; break }
  # minimize/restore trick beats the foreground-lock
  [Probe.Native]::ShowWindow($np.MainWindowHandle, 6) | Out-Null   # SW_MINIMIZE
  Start-Sleep -Milliseconds 200
  [Probe.Native]::ShowWindow($np.MainWindowHandle, 9) | Out-Null   # SW_RESTORE
  Start-Sleep -Milliseconds 400
  if ((Get-ForegroundTitle) -match "Notepad") { $activated = $true }
}
Start-Sleep -Milliseconds 300

$title = Get-ForegroundTitle
if ($title -notmatch "Notepad") {
  Write-Output "ABORT: foreground is '$title', not Notepad"
  exit 1
}
Write-Output "FOREGROUND: $title"

# Focused child hwnd (for E3 PostMessage target)
$fg = [Probe.Native]::GetForegroundWindow()
$pid2 = 0
$tid = [Probe.Native]::GetWindowThreadProcessId($fg, [ref]$pid2)
$gti = New-Object Probe.Native+GUITHREADINFO
$gti.cbSize = [Runtime.InteropServices.Marshal]::SizeOf([type][Probe.Native+GUITHREADINFO])
[Probe.Native]::GetGUIThreadInfo($tid, [ref]$gti) | Out-Null
Write-Output ("FOCUS-HWND: 0x{0:X} (thread {1})" -f $gti.hwndFocus.ToInt64(), $tid)
$target = if ($gti.hwndFocus -ne [IntPtr]::Zero) { $gti.hwndFocus } else { $fg }

# Clear current tab content (uses real modifiers, BEFORE the test stream)
Send-Vk 0x11 $true; Send-VkTap 0x41; Send-Vk 0x11 $false   # Ctrl+A
Start-Sleep -Milliseconds 120
Send-VkTap 0x2E                                             # Delete
Start-Sleep -Milliseconds 250

# --- The test stream ----------------------------------------------------------
$text = @'
Even though it's been a couple of decades since we worked together, I think back on the lessons that I learned from you all the time. 
'@
$text = $text.TrimEnd() + " "   # here-string strips trailing space; restore it
$chunks = @()
for ($i = 0; $i -lt $text.Length; $i += 8) {
  $chunks += $text.Substring($i, [Math]::Min(8, $text.Length - $i))
}
Write-Output "CHUNKS: $($chunks.Count) UNITS: $($text.Length)"

$WM_KEYDOWN = 0x100; $WM_KEYUP = 0x101
$guardLog = @()
for ($i = 0; $i -lt $chunks.Count; $i++) {
  # winpepper-style guard sample immediately before each chunk send
  $ctrlAsync = ([Probe.Native]::GetAsyncKeyState(0x11) -band 0x8000) -ne 0
  $guardLog += "chunk$($i+1)=$(if ($ctrlAsync) {'CTRL-DOWN'} else {'up'})"

  Send-UnicodeChunk $chunks[$i]

  if ($i -eq 0) {      # after chunk 1: assert Ctrl
    if ($Experiment -eq "E1") { Send-Vk 0x11 $true }
    else { [Probe.Native]::PostMessage($target, $WM_KEYDOWN, [IntPtr]0x11, [IntPtr]0x001D0001) | Out-Null }
    Write-Output "CTRL-ASSERTED after chunk 1 ($Experiment)"
  }
  if ($i -eq 5) {      # after chunk 6: release Ctrl
    if ($Experiment -eq "E1") { Send-Vk 0x11 $false }
    else { [Probe.Native]::PostMessage($target, $WM_KEYUP, [IntPtr]0x11, [IntPtr]([int64]0xC01D0001)) | Out-Null }
    Write-Output "CTRL-RELEASED after chunk 6 ($Experiment)"
  }
  Start-Sleep -Milliseconds 14
}
Write-Output "GUARD-SAMPLES: $($guardLog -join ' ')"

# Safety: ensure Ctrl is not left down (E1)
Send-Vk 0x11 $false
Start-Sleep -Milliseconds 400

# --- Readback 1: WM_GETTEXT to the focused edit child --------------------------
$sb = New-Object System.Text.StringBuilder 4096
[Probe.Native]::SendMessage($target, 0x000D, [IntPtr]4096, $sb) | Out-Null
$result = $sb.ToString()
Write-Output "WM_GETTEXT: [$result]"

# --- Readback 2 (fallback): select-all + copy ----------------------------------
if ([string]::IsNullOrEmpty($result)) {
  Set-Clipboard -Value "<<CLIPBOARD-SENTINEL>>"
  Send-Vk 0x11 $true; Send-VkTap 0x41; Start-Sleep -Milliseconds 120
  Send-VkTap 0x43; Send-Vk 0x11 $false                      # Ctrl+C
  Start-Sleep -Milliseconds 400
  $result = Get-Clipboard -Raw
  Write-Output "CLIPBOARD: [$result]"
}

Write-Output "EXPECTED: [$text]"
Write-Output "RESULT:   [$result]"
if ($result -ceq $text) { Write-Output "VERDICT: INTACT" }
else { Write-Output "VERDICT: CORRUPTED" }
