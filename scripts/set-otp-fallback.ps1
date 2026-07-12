# Sets fallback-to-Microsoft-email on the Fatwood OTP extension: if our email
# hook ever errors, Entra sends its default verification email instead of
# failing the sign-up. One PATCH; device-code sign-in with the admin account.
# Keep this file pure ASCII (PowerShell 5.1 parses BOM-less files as ANSI).
#
# Run:  powershell -ExecutionPolicy Bypass -File scripts/set-otp-fallback.ps1

$tenant = 'f0ae24f7-e027-456f-bd3d-ec966ffec496'
$clientId = '14d82eec-204b-4c2f-b7e8-296a70dab67e'
$scopes = 'https://graph.microsoft.com/CustomAuthenticationExtension.ReadWrite.All'
$extensionId = '72227086-d5a6-4440-b707-159f30dfe13b'

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
$body = '{"@odata.type":"#microsoft.graph.onOtpSendCustomExtension","behaviorOnError":{"@odata.type":"#microsoft.graph.fallbackToMicrosoftProviderOnError"}}'
Invoke-RestMethod -Method Patch -Uri "https://graph.microsoft.com/beta/identity/customAuthenticationExtensions/$extensionId" -Headers $h -Body $body | Out-Null

$check = Invoke-RestMethod -Uri "https://graph.microsoft.com/beta/identity/customAuthenticationExtensions/$extensionId" -Headers $h
Write-Host ("behaviorOnError is now: " + $check.behaviorOnError.'@odata.type') -ForegroundColor Green
Write-Host 'DONE - sign-ups fall back to Microsoft email if our sender ever fails.'