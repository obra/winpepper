param([ValidateSet("Edit","Terminal")][string]$Target = "Edit", [int]$Runs = 3)
$ErrorActionPreference = "Stop"
Add-Type -Name Native -Namespace Compat -MemberDefinition @'
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
[DllImport("user32.dll")]
public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
[DllImport("user32.dll")]
public static extern bool GetGUIThreadInfo(uint tid, ref GUITHREADINFO info);
[DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
[DllImport("user32.dll", CharSet=CharSet.Unicode)]
public static extern IntPtr SendMessage(IntPtr h, uint m, IntPtr w, System.Text.StringBuilder s);
[DllImport("user32.dll", CharSet=CharSet.Unicode, EntryPoint="SendMessageW")]
public static extern IntPtr SendMessageStr(IntPtr h, uint m, IntPtr w, string s);
[DllImport("user32.dll", SetLastError=true, EntryPoint="SendMessageTimeoutW")]
public static extern IntPtr SendMessageTimeout(IntPtr h, uint m, IntPtr w, IntPtr l, uint flags, uint timeout, out IntPtr result);
'@
function Get-FgTitle {
  $sb = New-Object System.Text.StringBuilder 512
  [Compat.Native]::GetWindowText([Compat.Native]::GetForegroundWindow(), $sb, 512) | Out-Null
  $sb.ToString()
}
function Get-FocusChild {
  $fg = [Compat.Native]::GetForegroundWindow()
  $p = 0
  $tid = [Compat.Native]::GetWindowThreadProcessId($fg, [ref]$p)
  $g = New-Object Compat.Native+GUITHREADINFO
  $g.cbSize = [Runtime.InteropServices.Marshal]::SizeOf([type][Compat.Native+GUITHREADINFO])
  [Compat.Native]::GetGUIThreadInfo($tid, [ref]$g) | Out-Null
  Write-Host ("FOCUS-HWND: 0x{0:X} (fg 0x{1:X})" -f $g.hwndFocus.ToInt64(), $fg.ToInt64())
  if ($g.hwndFocus -ne [IntPtr]::Zero) { $g.hwndFocus } else { $fg }
}
function Send-SmtoText([IntPtr]$h, [string]$s) {
  $fails = 0
  foreach ($ch in $s.ToCharArray()) {
    $r = [IntPtr]::Zero
    $ok = [Compat.Native]::SendMessageTimeout($h, 0x0102, [IntPtr][int][char]$ch, [IntPtr]1, 0x0002, 150, [ref]$r)
    if ($ok -eq [IntPtr]::Zero) { $fails++ }
  }
  if ($fails -gt 0) { Write-Output "  SMTO-FAILS: $fails" }
}
$text = @'
Even though it's been a couple of decades since we worked together, I think back on the lessons that I learned from you all the time. 
'@
$text = $text.TrimEnd() + " "

if ($Target -eq "Edit") {
  $hostScript = Join-Path $PSScriptRoot "edithost.ps1"
  for ($run = 1; $run -le $Runs; $run++) {
    $proc = Start-Process powershell -ArgumentList "-NoProfile","-STA","-ExecutionPolicy","Bypass","-File","`"$hostScript`"" -PassThru
    $deadline = (Get-Date).AddSeconds(8)
    while ((Get-Date) -lt $deadline -and (Get-FgTitle) -ne "WP-EDIT-HOST") { Start-Sleep -Milliseconds 250 }
    if ((Get-FgTitle) -ne "WP-EDIT-HOST") { Write-Output "run $run ABORT: fg='$(Get-FgTitle)'"; $proc | Stop-Process -Force; continue }
    $target = Get-FocusChild
    Send-SmtoText $target $text
    Start-Sleep -Milliseconds 300
    $sb = New-Object System.Text.StringBuilder 4096
    [Compat.Native]::SendMessage($target, 0x000D, [IntPtr]4096, $sb) | Out-Null
    $r = $sb.ToString()
    if ($r -ceq $text) { Write-Output "run $run [INTACT]" } else { Write-Output "run $run [CORRUPTED]"; Write-Output "  RESULT: [$r]" }
    $proc | Stop-Process -Force
    Start-Sleep -Milliseconds 400
  }
} else {
  # Terminal: functional readback -- inject an echo-redirect command + CR, check the file.
  for ($run = 1; $run -le $Runs; $run++) {
    $marker = "WPMARK $run alpha beta gamma $(Get-Random)"
    $out = Join-Path $env:TEMP "wp-term-$run.txt"
    Remove-Item $out -ErrorAction SilentlyContinue
    $proc = Start-Process cmd.exe -ArgumentList "/k" -PassThru
    $deadline = (Get-Date).AddSeconds(8)
    while ((Get-Date) -lt $deadline -and (Get-FgTitle) -notmatch 'cmd') { Start-Sleep -Milliseconds 300 }
    $title = Get-FgTitle
    Write-Output "run $run fg='$title'"
    if ($title -notmatch 'cmd') { Write-Output "run $run ABORT: terminal never took foreground"; if (-not $proc.HasExited) { $proc | Stop-Process -Force -ErrorAction SilentlyContinue }; continue }
    $target = Get-FocusChild
    $cmd = "echo $marker> `"$out`""
    Send-SmtoText $target $cmd
    Send-SmtoText $target ([string][char]0x0D)
    Start-Sleep -Milliseconds 1200
    if (Test-Path $out) {
      $got = (Get-Content $out -Raw).Trim()
      if ($got -ceq $marker) { Write-Output "run $run [INTACT] file content exact" }
      else { Write-Output "run $run [CORRUPTED] got: [$got]" }
    } else { Write-Output "run $run [NO-DELIVERY] file never created" }
    # close the terminal window (it is probe-owned)
    Send-SmtoText $target "exit"
    Send-SmtoText $target ([string][char]0x0D)
    Start-Sleep -Milliseconds 800
    if (-not $proc.HasExited) { $proc | Stop-Process -Force -ErrorAction SilentlyContinue }
    Remove-Item $out -ErrorAction SilentlyContinue
  }
}
