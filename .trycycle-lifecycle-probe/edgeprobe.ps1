# Chromium (isolated Edge) cell: SMTO WM_CHAR delivery + EM gate-out evidence.
# Uses --user-data-dir so the user's own Edge/Chrome sessions are untouched.
# Readback via document.title mirror (no clipboard involvement).
param([int]$Runs = 2)
$ErrorActionPreference = "Stop"
Add-Type -Name Native -Namespace Edge -MemberDefinition @'
[StructLayout(LayoutKind.Sequential)]
public struct GUITHREADINFO {
  public uint cbSize; public uint flags;
  public IntPtr hwndActive; public IntPtr hwndFocus; public IntPtr hwndCapture;
  public IntPtr hwndMenuOwner; public IntPtr hwndMoveSize; public IntPtr hwndCaret;
  public int l; public int t; public int r; public int b;
}
[DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
[DllImport("user32.dll", CharSet=CharSet.Unicode)]
public static extern int GetWindowText(IntPtr h, System.Text.StringBuilder s, int n);
[DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
[DllImport("user32.dll")] public static extern bool GetGUIThreadInfo(uint tid, ref GUITHREADINFO info);
[DllImport("user32.dll", CharSet=CharSet.Unicode)]
public static extern int GetClassName(IntPtr h, System.Text.StringBuilder s, int n);
[DllImport("user32.dll", SetLastError=true, EntryPoint="SendMessageTimeoutW")]
public static extern IntPtr SendMessageTimeout(IntPtr h, uint m, IntPtr w, IntPtr l, uint flags, uint timeout, out IntPtr result);
[DllImport("user32.dll", CharSet=CharSet.Unicode, EntryPoint="SendMessageW")]
public static extern IntPtr SendMessageStr(IntPtr h, uint m, IntPtr w, string s);
'@
function FgTitle { $sb = New-Object System.Text.StringBuilder 1024; [Edge.Native]::GetWindowText([Edge.Native]::GetForegroundWindow(), $sb, 1024) | Out-Null; $sb.ToString() }
$text = "Even though it's been a couple of decades since we worked together. "
$html = Join-Path $env:TEMP "wp-chromium-test.html"
Set-Content -Path $html -Value '<html><head><title>WPEDGEPROBE</title></head><body><textarea autofocus style="width:95%;height:300px" oninput="document.title=this.value"></textarea></body></html>' -Encoding UTF8
$prof = Join-Path $env:TEMP "wp-edge-profile"
for ($run = 1; $run -le $Runs; $run++) {
  Start-Process msedge.exe -ArgumentList "--user-data-dir=`"$prof`"","--no-first-run","--new-window","file:///$($html -replace '\\','/')"
  $deadline = (Get-Date).AddSeconds(15)
  while ((Get-Date) -lt $deadline -and (FgTitle) -notmatch 'WPEDGEPROBE') { Start-Sleep -Milliseconds 400 }
  if ((FgTitle) -notmatch 'WPEDGEPROBE') { Write-Output "run $run ABORT fg='$(FgTitle)'"; break }
  Start-Sleep -Milliseconds 800
  $fg = [Edge.Native]::GetForegroundWindow()
  $p = 0; $tid = [Edge.Native]::GetWindowThreadProcessId($fg, [ref]$p)
  $g = New-Object Edge.Native+GUITHREADINFO
  $g.cbSize = [Runtime.InteropServices.Marshal]::SizeOf([type][Edge.Native+GUITHREADINFO])
  [Edge.Native]::GetGUIThreadInfo($tid, [ref]$g) | Out-Null
  $tgt = if ($g.hwndFocus -ne [IntPtr]::Zero) { $g.hwndFocus } else { $fg }
  $cls = New-Object System.Text.StringBuilder 128
  [Edge.Native]::GetClassName($tgt, $cls, 128) | Out-Null
  $selR = [IntPtr]::Zero
  $selOk = [Edge.Native]::SendMessageTimeout($tgt, 0x00B0, [IntPtr]::Zero, [IntPtr]::Zero, 0x0002, 150, [ref]$selR)
  Write-Output ("run $run gate: class=[{0}] em_getsel_ok={1} sel=0x{2:X} focus0={3}" -f $cls.ToString(), ($selOk -ne [IntPtr]::Zero), $selR.ToInt64(), ($g.hwndFocus -eq [IntPtr]::Zero))
  # EM_REPLACESEL no-delivery evidence
  [Edge.Native]::SendMessageStr($tgt, 0x00C2, [IntPtr]1, "EMPROBE") | Out-Null
  Start-Sleep -Milliseconds 300
  $afterEm = FgTitle
  Write-Output ("run $run em_replacesel_effect: title=[{0}]" -f $afterEm.Substring(0, [Math]::Min(30, $afterEm.Length)))
  # SMTO WM_CHAR delivery
  $fails = 0
  foreach ($ch in $text.ToCharArray()) {
    $r = [IntPtr]::Zero
    $ok = [Edge.Native]::SendMessageTimeout($tgt, 0x0102, [IntPtr][int][char]$ch, [IntPtr]1, 0x0002, 150, [ref]$r)
    if ($ok -eq [IntPtr]::Zero) { $fails++ }
  }
  Start-Sleep -Milliseconds 700
  $title = FgTitle
  if ($title -ceq $text) { Write-Output "run $run [INTACT] smto_fails=$fails" }
  elseif ($title -match 'WPEDGEPROBE') { Write-Output "run $run [NO-DELIVERY] smto_fails=$fails" }
  else { Write-Output "run $run [PARTIAL/CORRUPT] smto_fails=$fails title=[$title]" }
  Get-CimInstance Win32_Process -Filter "Name='msedge.exe'" | Where-Object { $_.CommandLine -like "*wp-edge-profile*" } | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
  Start-Sleep -Milliseconds 800
}
Remove-Item $html -ErrorAction SilentlyContinue
