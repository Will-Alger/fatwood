# Adds the four DNS records Azure Communication Services needs to send email
# as @fatwood.io: domain-verification TXT, SPF TXT, and two DKIM CNAMEs
# (DNS-only — proxying breaks DKIM validation). One-time setup; records must
# stay in place. Run:
#   powershell -ExecutionPolicy Bypass -File scripts/add-acs-email-dns.ps1 -CloudflareToken <token>
param([Parameter(Mandatory = $true)][string]$CloudflareToken)

$h = @{ Authorization = "Bearer $CloudflareToken"; 'Content-Type' = 'application/json' }
$zone = 'fa1184fa7928952412597e4dc4c87bd4' # fatwood.io

$records = @(
    @{ type = 'TXT'; name = 'fatwood.io'; content = '"ms-domain-verification=b49837f4-8371-456e-bfe5-bd1577b1cfc8"'; ttl = 3600 },
    @{ type = 'TXT'; name = 'fatwood.io'; content = '"v=spf1 include:spf.protection.outlook.com -all"'; ttl = 3600 },
    @{ type = 'CNAME'; name = 'selector1-azurecomm-prod-net._domainkey'; content = 'selector1-azurecomm-prod-net._domainkey.azurecomm.net'; ttl = 3600; proxied = $false },
    @{ type = 'CNAME'; name = 'selector2-azurecomm-prod-net._domainkey'; content = 'selector2-azurecomm-prod-net._domainkey.azurecomm.net'; ttl = 3600; proxied = $false }
)

foreach ($r in $records) {
    try {
        $resp = Invoke-RestMethod -Method Post -Uri "https://api.cloudflare.com/client/v4/zones/$zone/dns_records" -Headers $h -Body ($r | ConvertTo-Json)
        Write-Output "added: $($r.type) $($r.name)"
    } catch {
        $reader = New-Object System.IO.StreamReader($_.Exception.Response.GetResponseStream())
        Write-Output "FAILED $($r.type) $($r.name): $($reader.ReadToEnd())"
    }
}