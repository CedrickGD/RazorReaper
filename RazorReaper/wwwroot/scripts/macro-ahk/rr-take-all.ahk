F5::ExitApp
Q::
if Running
{
Running := false
Return
}
Running := true

{
		send v
                Sleep 200
		MouseClick, left, 1071, 273
		Send {Click Left}
                Send {Click Left}
                Send {Click Left}
                MouseClick, left, 720, 540


}


Running := false
return
