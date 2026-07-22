On Error Resume Next

Dim shell, folder
Set shell = CreateObject("Shell.Application")
Set folder = shell.BrowseForFolder(0, "Select image folder", 0, 0)

If Err.Number <> 0 Then
  WScript.Echo ""
  WScript.Quit 1
End If

If folder Is Nothing Then
  WScript.Echo ""
Else
  WScript.Echo folder.Self.Path
End If
