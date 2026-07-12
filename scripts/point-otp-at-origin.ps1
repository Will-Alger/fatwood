# Re-points the Entra OTP email extension at the app's direct Azure origin,
# bypassing Cloudflare: Bot Fight Mode (free plan, no path exemptions) was
# challenging Microsoft's webhook callout (proven via firewall events), and
# the endpoint needs no bot screening - it only accepts Entra's signed
# tokens. Public traffic keeps full Cloudflare protection.
# Keep this file pure ASCII (PowerShell 5.1 parses BOM-less files as ANSI).
#
# Run:  powershell -ExecutionPolicy Bypass -File scripts/point-otp-at-origin.ps1

$tenant = 'f0ae24f7-e027-456f-bd3d-ec966ffec496'
$clientId = '14d82eec-204b-4c2f-b7e8-296a70dab67e'
$scopes = 'https://graph.microsoft.com/CustomAuthenticationExtension.ReadWrite.All'
$extensionId = '72227086-d5a6-4440-b707-159f30dfe13b'
$originUrl = 'https://rdisc-api.gentleflower-aa2882c7.centralus.azurecontainerapps.io/api/auth-events/otp-send'

$dc = Invoke-RestMethod -Method Post -Uri "https://login.microsoftonline.com/$tenant/oauth2/v2.0/devicecode" -Body @{ client_id = $clientId; scope = $scopes }
Write-Host ''
Write-Host (">>> Open " + $dc.verification_uri + " and enter code: " + $dc.user_code) -ForegroundColor Yellow
Write-Host ''

$token = $null
$deadline = (Get-Date).AddSeconds($dc.expires_in)
while ((Get-Date) -lt $deadline) {
    Start-Sleep -Seconds $dc.interval
    try {
        $resp = Invoke-RestMethod -Method Post -Uri "https://login.microsoftonline.com/$tenant/oauth2/v2.0/token" -Body @{
            grant_type = 'urn:ietf:params:oauth:grant-type:device_code'
            client_id  = $clientId
            device_code = $dc.device_code
        }
        $token = $resp.access_token
        break
    } catch {
        $err = ($_.ErrorDetails.Message | ConvertFrom-Json).error
        if ($err -ne 'authorization_pending') { throw "Device login failed: $err" }
    }
}
if (-not $token) { throw 'Device login timed out.' }

$h = @{ Authorization = "Bearer $token"; 'Content-Type' = 'application/json' }
$body = @"
{
  "@odata.type": "#microsoft.graph.onOtpSendCustomExtension",
  "endpointConfiguration": {
    "@odata.type": "#microsoft.graph.httpRequestEndpoint",
    "targetUrl": "$originUrl"
  }
}
"@
Invoke-RestMethod -Method Patch -Uri "https://graph.microsoft.com/beta/identity/customAuthenticationExtensions/$extensionId" -Headers $h -Body $body | Out-Null

$check = Invoke-RestMethod -Uri "https://graph.microsoft.com/beta/identity/customAuthenticationExtensions/$extensionId" -Headers $h
Write-Host ("targetUrl is now: " + $check.endpointConfiguration.targetUrl) -ForegroundColor Green
Write-Host 'DONE - retry Forgot password in a couple of minutes; the code should arrive as the Fatwood-branded email.'