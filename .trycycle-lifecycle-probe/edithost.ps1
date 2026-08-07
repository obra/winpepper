Add-Type -AssemblyName System.Windows.Forms
$f = New-Object Windows.Forms.Form
$f.Text = "WP-EDIT-HOST"
$t = New-Object Windows.Forms.TextBox
$t.Multiline = $true; $t.Dock = 'Fill'
$f.Controls.Add($t)
$f.Add_Shown({ $t.Focus() })
[Windows.Forms.Application]::Run($f)
