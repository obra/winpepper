' Read the probe's KEY=VALUE output file (written by RunCapabilityProbe)
' and copy each KEY=VALUE pair into MSI session properties. Generic parser
' so any future probe key flows through without a wxs edit.
Dim fso, f, line, kv, path
Set fso = CreateObject("Scripting.FileSystemObject")
path = Session.Property("TempFolder") & "winpepper-probe.txt"
If Not fso.FileExists(path) Then
  path = Environ("TEMP") & "\winpepper-probe.txt"
End If
If fso.FileExists(path) Then
  Set f = fso.OpenTextFile(path, 1, False)
  Do Until f.AtEndOfStream
    line = Trim(f.ReadLine)
    kv = Split(line, "=", 2)
    If UBound(kv) = 1 Then Session.Property(kv(0)) = kv(1)
  Loop
  f.Close
End If
