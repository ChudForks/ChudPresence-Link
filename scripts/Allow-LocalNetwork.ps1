# Run manually from an elevated PowerShell only if Windows Firewall blocks phone connections.
param([int]$Port = 17879)
$ErrorActionPreference = 'Stop'
if ($Port -lt 1024 -or $Port -gt 65535) { throw 'Invalid port.' }
New-NetFirewallRule -DisplayName "ChudPresence Link (private LAN, $Port)" -Direction Inbound -Action Allow -Protocol TCP -LocalPort $Port -RemoteAddress LocalSubnet -Profile Private
