"""Child-process launch helpers.

Every CAD stage shells out to accoreconsole.exe, dotnet or git. On Windows a
console child allocates a visible console window, which pops up on the user's
desktop for the whole run. All launch sites route through hidden_kwargs() so
children start with no window and never steal focus.
"""
from __future__ import annotations

import subprocess
import sys

CREATE_NO_WINDOW = 0x08000000
SW_HIDE = 0


def hidden_kwargs(extra_flags: int = 0) -> dict:
    """Popen/Run keyword arguments that suppress the child's console window."""
    if sys.platform != "win32":
        return {"creationflags": extra_flags} if extra_flags else {}

    flags = getattr(subprocess, "CREATE_NO_WINDOW", CREATE_NO_WINDOW) | extra_flags
    startup = subprocess.STARTUPINFO()
    startup.dwFlags |= subprocess.STARTF_USESHOWWINDOW
    startup.wShowWindow = SW_HIDE
    return {"creationflags": flags, "startupinfo": startup}


def hidden_run(command, **kwargs):
    """subprocess.run with hidden console defaults (caller kwargs win)."""
    merged = hidden_kwargs(int(kwargs.pop("creationflags", 0) or 0))
    merged.update(kwargs)
    return subprocess.run(command, **merged)


def hidden_popen(command, **kwargs):
    """subprocess.Popen with hidden console defaults (caller kwargs win)."""
    merged = hidden_kwargs(int(kwargs.pop("creationflags", 0) or 0))
    merged.update(kwargs)
    return subprocess.Popen(command, **merged)
