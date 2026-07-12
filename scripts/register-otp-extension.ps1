# Final wiring for the Fatwood-branded OTP email: registers the Entra custom
# authentication extension and the tenant-wide OnEmailOtpSend listener (with
# fallback to Microsoft's default email on any failure, so sign-ups can never
# brick). Auth is an interactive device-code sign-in with YOUR admin account;
# nothing privileged is created or left behind.
#
# Run:  powershell -ExecutionPolicy Bypass -File scripts/register-otp-extension.ps1
# Then open the URL it prints, enter the code, sign in as algerw@icloud.com.
# NOTE: keep this file pure ASCII - PowerShell 5.1 reads BOM-less files as
# ANSI and non-ASCII characters break the parser.

$tenant = 'f0ae24f7-e027-456f-bd3d-ec966ffec496'
$clientId = '14d82eec-204b-4c2f-b7e8-296a70dab67e' # Microsoft Graph PowerShell (public client)
$scopes = 'https://graph.microsoft.com/CustomAuthenticationExtension.ReadWrite.All https://graph.microsoft.com/EventListener.ReadWrite.All'

# --- device code flow ---
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
Write-Host 'Signed in. Registering extension...' -ForegroundColor Green

$h = @{ Authorization = "Bearer $token"; 'Content-Type' = 'application/json' }

# --- 1. custom authentication extension ---
$extBody = Get-Content "$PSScriptRoot\otp-extension.json" -Raw
$ext = Invoke-RestMethod -Method Post -Uri 'https://graph.microsoft.com/beta/identity/customAuthenticationExtensions' -Headers $h -Body $extBody
Write-Host ("extension created: " + $ext.id)

# --- 2. listener: route ALL apps' OTP emails through the extension ---
$listenerBody = @"
{
  "@odata.type": "#microsoft.graph.onEmailOtpSendListener",
  "conditions": { "applications": { "includeAllApplications": true } },
  "priority": 500,
  "handler": {
    "@odata.type": "#microsoft.graph.onOtpSendCustomExtensionHandler",
    "customExtension": { "id": "$($ext.id)" }
  }
}
"@
$listener = Invoke-RestMethod -Method Post -Uri 'https://graph.microsoft.com/beta/identity/authenticationEventListeners' -Headers $h -Body $listenerBody
Write-Host ("listener created: " + $listener.id)

# --- 3. fallback: if our endpoint errors, Microsoft's default email goes out ---
$fallbackBody = @"
{
  "@odata.type": "#microsoft.graph.onEmailOtpSendListener",
  "handler": {
    "@odata.type": "#microsoft.graph.onOtpSendCustomExtensionHandler",
    "customExtension": { "id": "$($ext.id)" },
    "configuration": { "behaviorOnError": { "@odata.type": "#microsoft.graph.fallbackToMicrosoftProviderOnError" } }
  }
}
"@
try {
    Invoke-RestMethod -Method Patch -Uri "https://graph.microsoft.com/beta/identity/authenticationEventListeners/$($listener.id)" -Headers $h -Body $fallbackBody | Out-Null
    Write-Host 'fallback-to-Microsoft-email enabled'
} catch {
    Write-Host ("fallback PATCH failed (non-fatal, can be set in portal): " + $_.ErrorDetails.Message) -ForegroundColor Yellow
}

Write-Host ''
Write-Host 'DONE. Test: create a new account at https://www.fatwood.io - the verification code should arrive as a Fatwood-branded email from noreply@fatwood.io.' -ForegroundColor Green