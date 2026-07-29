' Watchdog launcher for the Pinscreen 2 library server.
'
' Runs the server hidden and relaunches it whenever it exits. This exists
' because a single crash used to take the library offline indefinitely -- the
' old Startup shortcut fired the exe exactly once at login, so when the server
' died it simply stayed dead until somebody noticed.
'
' Root folder and port come from server-config.json next to the exe, and the
' server writes its own server.log, so nothing needs to be substituted here.
'
' A SYSTEM scheduled task additionally survives logout and starts before login;
' install that instead with: scripts/install-server.ps1 -AsService (elevated).
Option Explicit
Dim sh, fso, dir, cmd
Set sh  = CreateObject("WScript.Shell")
Set fso = CreateObject("Scripting.FileSystemObject")
dir = fso.GetParentFolderName(WScript.ScriptFullName)
sh.CurrentDirectory = dir
cmd = """" & dir & "\Pinscreen2.Server.exe"""

Do
  ' 0 = hidden window, True = block until the server process exits.
  sh.Run cmd, 0, True
  ' Pause before relaunching so a hard startup failure cannot spin the CPU.
  WScript.Sleep 10000
Loop
