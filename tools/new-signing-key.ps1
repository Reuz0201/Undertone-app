# Создаёт ключ подписи обновлений Undertone (ECDSA P-256).
# Запуск: pwsh -File tools\new-signing-key.ps1   (нужен PowerShell 7: winget install Microsoft.PowerShell)
#
# Приватный ключ -> секрет GitHub UPDATE_SIGNING_KEY (Settings → Secrets and variables → Actions → New repository secret)
# Публичный ключ -> переменная GitHub UPDATE_PUBLIC_KEY (там же, вкладка Variables)
# После этого каждый релиз подписывается, а приложения, собранные с этим ключом, примут только подписанные обновления.
# Приватный ключ никому не показывай и не клади в репозиторий. Потеряешь его — придётся выпускать сборку с новым ключом.

$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSVersion.Major -lt 7) {
    Write-Host '  Нужен PowerShell 7: установи командой  winget install Microsoft.PowerShell  и запусти через pwsh.' -ForegroundColor Red
    exit 1
}
$ecdsa = [System.Security.Cryptography.ECDsa]::Create([System.Security.Cryptography.ECCurve+NamedCurves]::nistP256)

$private = [Convert]::ToBase64String($ecdsa.ExportPkcs8PrivateKey(), 'InsertLineBreaks')
$pem = "-----BEGIN PRIVATE KEY-----`n$private`n-----END PRIVATE KEY-----"
$public = [Convert]::ToBase64String($ecdsa.ExportSubjectPublicKeyInfo())

$out = Join-Path $PSScriptRoot 'signing-key.private.pem'
Set-Content -Path $out -Value $pem -Encoding ascii

Write-Host ''
Write-Host '  Приватный ключ сохранён в:' -ForegroundColor Cyan
Write-Host "  $out"
Write-Host '  -> скопируй ВСЁ содержимое файла в секрет UPDATE_SIGNING_KEY, потом удали файл.' -ForegroundColor Yellow
Write-Host ''
Write-Host '  Публичный ключ (переменная UPDATE_PUBLIC_KEY):' -ForegroundColor Cyan
Write-Host "  $public"
Write-Host ''
