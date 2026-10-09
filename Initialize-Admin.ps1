param([string]$Email)
$ErrorActionPreference = 'Stop'
if (-not $Email) { $Email = Read-Host 'Correo de la primera cuenta administrativa' }
$taskSecurePassword = Read-Host 'Contraseña (12 caracteres, mayúscula, minúscula, número y símbolo)' -AsSecureString
$taskPointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($taskSecurePassword)
try {
    $env:MA_ADMIN_PASSWORD = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($taskPointer)
    dotnet run --project "$PSScriptRoot\ManagementAppointment\ManagementAppointment.csproj" --no-launch-profile -- --clinic-admin $Email
    if ($LASTEXITCODE -ne 0) { throw 'No se pudo crear la primera cuenta administrativa.' }
} finally {
    [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($taskPointer)
    Remove-Item Env:\MA_ADMIN_PASSWORD -ErrorAction SilentlyContinue
}
