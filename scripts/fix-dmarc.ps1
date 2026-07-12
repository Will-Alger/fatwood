# Replaces the leftover GoDaddy-parking DMARC record (p=quarantine, reports
# to onsecureserver.net) with a sane warm-up policy for a new sending domain:
# p=none (monitor, don't punish) and aggregate reports to admin@fatwood.io.
# Keep this file pure ASCII (PowerShell 5.1 parses BOM-less files as ANSI).
#
# Run:  powershell -ExecutionPolicy Bypass -File scripts/fix-dmarc.ps1 -CloudflareToken <token>
param([Parameter(Mandatory = $true)][string]$CloudflareToken)

$h = @{ Authorization = "Bearer $CloudflareToken"; 'Content-Type' = 'application/json' }
$zone = 'fa1184fa7928952412597e4dc4c87bd4' # fatwood.io

$existing = (Invoke-RestMethod -Uri "https://api.cloudflare.com/client/v4/zones/$zone/dns_records?type=TXT" -Headers $h).result |
    Where-Object { $_.name -eq '_dmarc.fatwood.io' }

$body = @{
    type    = 'TXT'
    name    = '_dmarc'
    content = '"v=DMARC1; p=none; rua=mailto:admin@fatwood.io"'
    ttl     = 3600
} | ConvertTo-Json

if ($existing) {
    Invoke-RestMethod -Method Put -Uri "https://api.cloudflare.com/client/v4/zones/$zone/dns_records/$($existing.id)" -Headers $h -Body $body | Out-Null
    Write-Output 'DMARC record REPLACED: p=none, reports to admin@fatwood.io'
} else {
    Invoke-RestMethod -Method Post -Uri "https://api.cloudflare.com/client/v4/zones/$zone/dns_records" -Headers $h -Body $body | Out-Null
    Write-Output 'DMARC record CREATED: p=none, reports to admin@fatwood.io'
}