param(
    [string]$Godot = "godot",
    [ValidateSet("project", "opengl3_angle", "opengl3")]
    [string]$Driver = "project",
    [int]$Seed = 1981019679,
    [ValidatePattern("^[A-Za-z0-9]+$")]
    [string]$Size = "160x96",
    [ValidateRange(10, 600)]
    [int]$TimeoutSeconds = 90
)

$ErrorActionPreference = "Stop"
if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) {
    throw "The isolated native resize smoke test requires Windows."
}

if (-not ("TermCity.WindowsSmokeNative" -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;

namespace TermCity {
    public static class WindowsSmokeNative {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct Startup {
            public uint Size;
            public string Reserved, Desktop, Title;
            public uint X, Y, Width, Height, CharsX, CharsY, Fill, Flags;
            public ushort Show, ReservedSize;
            public IntPtr ReservedPtr, Input, Output, Error;
        }
        [StructLayout(LayoutKind.Sequential)]
        public struct ProcessInfo {
            public IntPtr Process, Thread;
            public uint ProcessId, ThreadId;
        }
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr CreateDesktop(string name, IntPtr device, IntPtr mode,
            uint flags, uint access, IntPtr attributes);
        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool CloseDesktop(IntPtr desktop);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool CreateProcess(string application, StringBuilder command,
            IntPtr processAttributes, IntPtr threadAttributes, bool inherit, uint flags,
            IntPtr environment, string directory, ref Startup startup, out ProcessInfo process);
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool GetExitCodeProcess(IntPtr handle, out uint code);
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool CloseHandle(IntPtr handle);
    }
}
'@
}

$executable = (Get-Command $Godot -CommandType Application).Source
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root "godot"
$desktopName = "TermCityResize_" + [Guid]::NewGuid().ToString("N")
$log = Join-Path ([IO.Path]::GetTempPath()) ($desktopName + ".log")
$desktop = [TermCity.WindowsSmokeNative]::CreateDesktop(
    $desktopName, [IntPtr]::Zero, [IntPtr]::Zero, 0, 0x01FF, [IntPtr]::Zero)
if ($desktop -eq [IntPtr]::Zero) {
    throw [ComponentModel.Win32Exception]::new([Runtime.InteropServices.Marshal]::GetLastWin32Error())
}

$info = New-Object TermCity.WindowsSmokeNative+ProcessInfo
try {
    $startup = New-Object TermCity.WindowsSmokeNative+Startup
    $startup.Size = [Runtime.InteropServices.Marshal]::SizeOf($startup)
    $startup.Desktop = "WinSta0\$desktopName"
    $renderer = if ($Driver -eq "project") { "" } else {
        "--rendering-method gl_compatibility --rendering-driver $Driver"
    }
    $expectedDriver = if ($Driver -eq "project") { "opengl3" } else { $Driver }
    $command = [Text.StringBuilder]::new(
        "`"$executable`" --path `"$project`" --log-file `"$log`" " +
        "$renderer -- " +
        "--smoke-test --seed $Seed --size $Size")
    if (-not [TermCity.WindowsSmokeNative]::CreateProcess(
        $executable, $command, [IntPtr]::Zero, [IntPtr]::Zero, $false, 0,
        [IntPtr]::Zero, $root, [ref]$startup, [ref]$info)) {
        throw [ComponentModel.Win32Exception]::new([Runtime.InteropServices.Marshal]::GetLastWin32Error())
    }
    Write-Output "Isolated desktop=$desktopName process=$($info.ProcessId) log=$log"
    $wait = [TermCity.WindowsSmokeNative]::WaitForSingleObject($info.Process, $TimeoutSeconds * 1000)
    if ($wait -eq 258) { throw "Godot resize smoke exceeded the $TimeoutSeconds second timeout. Log: $log" }
    if ($wait -ne 0) {
        throw [ComponentModel.Win32Exception]::new([Runtime.InteropServices.Marshal]::GetLastWin32Error())
    }
    [uint32]$exitCode = 0
    if (-not [TermCity.WindowsSmokeNative]::GetExitCodeProcess($info.Process, [ref]$exitCode)) {
        throw [ComponentModel.Win32Exception]::new([Runtime.InteropServices.Marshal]::GetLastWin32Error())
    }
    if ($exitCode -ne 0) { throw "Godot resize smoke exited with code $exitCode. Log: $log" }
    $content = Get-Content -LiteralPath $log -Raw
    if ($content -match "(?m)^ERROR:") { throw "Godot resize smoke logged an error. Log: $log" }
    foreach ($marker in @("TERMCITY_WINDOWS_NATIVE_MODAL_RESIZE_OK", "TERMCITY_WINDOWS_REPAINT_OK",
        "TERMCITY_WINDOWS_RESIZE_OK driver=$expectedDriver ", "TERMCITY_GODOT_SMOKE_OK")) {
        if (-not $content.Contains($marker)) { throw "Godot resize smoke is missing $marker. Log: $log" }
    }
} finally {
    try {
        if ($info.Process -ne [IntPtr]::Zero) {
            $wait = [TermCity.WindowsSmokeNative]::WaitForSingleObject($info.Process, 0)
            if ($wait -eq 258) {
                $game = Get-Process -Id $info.ProcessId
                try {
                    Stop-Process -Id $info.ProcessId
                    if (-not $game.WaitForExit(5000)) { throw "Diagnostic Godot did not stop within five seconds." }
                } finally { $game.Dispose() }
            } elseif ($wait -ne 0) {
                throw [ComponentModel.Win32Exception]::new([Runtime.InteropServices.Marshal]::GetLastWin32Error())
            }
        }
    } finally {
        $cleanupErrors = @()
        if ($info.Thread -ne [IntPtr]::Zero -and -not [TermCity.WindowsSmokeNative]::CloseHandle($info.Thread)) {
            $cleanupErrors += "Could not close the diagnostic thread handle."
        }
        if ($info.Process -ne [IntPtr]::Zero -and -not [TermCity.WindowsSmokeNative]::CloseHandle($info.Process)) {
            $cleanupErrors += "Could not close the diagnostic process handle."
        }
        if (-not [TermCity.WindowsSmokeNative]::CloseDesktop($desktop)) {
            $cleanupErrors += "Could not close the private diagnostic desktop."
        }
        if (Test-Path -LiteralPath $log) { Get-Content -LiteralPath $log -Tail 25 }
        if ($cleanupErrors.Count -gt 0) { throw ($cleanupErrors -join " ") }
    }
}
