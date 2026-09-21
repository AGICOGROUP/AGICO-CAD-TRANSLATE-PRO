' Run a command line with no visible console window.
' Usage: wscript.exe //nologo scripts\run_hidden.vbs "<command line>"
' Window style 0 = hidden, and the launcher does not wait.
Option Explicit
Dim shell, command
If WScript.Arguments.Count = 0 Then
    WScript.Echo "usage: run_hidden.vbs ""<command line>"""
    WScript.Quit 2
End If
command = WScript.Arguments(0)
Set shell = CreateObject("WScript.Shell")
shell.Run command, 0, False
