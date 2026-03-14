F5::ExitApp  ; Exit the script

F4:: ; Start / stop
if Running
{
    Running := false
    return
}
Running := true

SetTimer, AutomateKeys, 2000
return

AutomateKeys:
if Running
{
    Send, {Space}
    Sleep, 200
    Send, c
}
return
