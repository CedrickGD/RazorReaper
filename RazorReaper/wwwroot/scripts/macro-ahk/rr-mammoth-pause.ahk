F5::ExitApp  ; Exit the script

F4:: ; Start / stop
if Running
{
    Running := false
    return
}
Running := true
StartTime := A_TickCount
SetTimer, AutomateKeys, 1550
return

AutomateKeys:
if !Running
    return

if (A_TickCount - StartTime >= 240000)
{
    ToolTip, Pause - 24 seconds...
    Sleep, 24000
    ToolTip
    StartTime := A_TickCount
}

Send, {Space}
Sleep, 200
Send, c
return
