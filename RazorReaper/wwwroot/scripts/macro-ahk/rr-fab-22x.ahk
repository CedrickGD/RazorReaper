F5::ExitApp
F4::
if Running
{

Running := false
Return
}
Running := true

Loop,
{
	Loop,22
	{
		Sleep 1000
		send v
		Sleep 1000
		MouseClick, left, 974, 279
		Sleep 1000
		Send {A}
		Send {d}
		Send {v}
		Sleep 1000
		MouseClick, left, 1172, 339
		Sleep 1000
		Loop,10
		{
			Send a
			Sleep 200
		}
		Sleep 1000
		Send f
		Sleep 1000
		Send {a Down}
		Sleep 275
		Send {a Up}
		Sleep 1000
	}
	Sleep 1000
	Send {d Down}
	Sleep 15000
	Send {d Up}
}


Running := false
return
