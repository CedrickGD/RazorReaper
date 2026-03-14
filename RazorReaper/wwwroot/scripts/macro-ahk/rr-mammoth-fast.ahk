F5::ExitApp  ; Exit the script

F4:: ; Start / stop
if Running
{
    Running := false
    return
}
Running := true

SetTimer, AutomateClickAndC, 685
return

AutomateClickAndC:
if Running
{
    Click
    Sleep, 90
    Send, c
}
return
