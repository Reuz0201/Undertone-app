# Undertone — публикация новой версии.
# Запуск: двойной клик по publish.bat (или: publish.bat 2.0.0 "Описание")
param(
    [string]$Version,
    [string]$Message
)

# Continue, а не Stop: git пишет прогресс в stderr, и Windows PowerShell 5.1 при Stop считал бы это ошибкой.
# Коды возврата проверяем вручную.
$ErrorActionPreference = 'Continue'
Set-Location -LiteralPath $PSScriptRoot
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

$csproj = 'Undertone\Undertone.csproj'

function Step($text) { Write-Host "`n  > $text" -ForegroundColor Cyan }
function Ok($text)   { Write-Host "  [OK] $text" -ForegroundColor Green }
function Warn($text) { Write-Host "  [!] $text" -ForegroundColor Yellow }
function Fail($text) {
    Write-Host "`n  [X] $text" -ForegroundColor Red
    Write-Host ''
    Read-Host '  Нажми Enter, чтобы закрыть' | Out-Null
    exit 1
}
function Invoke-Git {
    $out = & git @args 2>&1 | ForEach-Object { "$_" }
    if ($LASTEXITCODE -ne 0) { throw "git $($args -join ' '): $($out -join ' ')" }
    return ($out -join "`n")
}

Write-Host ''
Write-Host '   _   _ _  _ ___  ___ ___ _____ ___  _  _ ___ ' -ForegroundColor White
Write-Host '  | | | | \| |   \| __| _ \_   _/ _ \| \| | __|' -ForegroundColor White
Write-Host '  | |_| | .` | |) | _||   / | || (_) | .` | _| ' -ForegroundColor White
Write-Host '   \___/|_|\_|___/|___|_|_\ |_| \___/|_|\_|___|' -ForegroundColor White
Write-Host '                 ~ публикация релиза ~' -ForegroundColor DarkGray

# ── 1. Проверки окружения ─────────────────────────────────────
Step 'Проверяю git и репозиторий'
if (-not (Get-Command git -ErrorAction SilentlyContinue)) { Fail 'git не установлен.' }
if (-not (Test-Path '.git')) { Fail 'Здесь нет git-репозитория. Запусти скрипт из корня проекта (рядом со start.bat).' }
if (-not (Test-Path $csproj)) { Fail "Не найден $csproj" }

# git может не доверять папке на другом диске/сетевом диске («dubious ownership») — лечится одной настройкой
$probe = (& git status --porcelain 2>&1 | ForEach-Object { "$_" }) -join "`n"
if ($probe -match 'dubious ownership') {
    $here = (Get-Location).Path -replace '\\', '/'
    Warn "git не доверяет папке $here (она на другом диске или создана другим пользователем)."
    $fix = Read-Host '  Добавить её в доверенные? (y/n)'
    if ($fix -notmatch '^(y|д|yes|да)$') { Fail 'Без этого git здесь не работает.' }
    & git config --global --add safe.directory $here
    Ok 'Папка добавлена в доверенные'
}

$remote = (& git remote get-url origin 2>&1 | ForEach-Object { "$_" }) -join ''
if ($LASTEXITCODE -ne 0 -or -not $remote) {
    Warn "У репозитория не настроен адрес на GitHub (origin)."
    Write-Host "  git сказал: $remote" -ForegroundColor DarkGray
    $default = 'https://github.com/reuz0201/Undertone-app.git'
    $url = Read-Host "  Адрес репозитория [Enter = $default]"
    if (-not $url) { $url = $default }
    & git remote add origin $url 2>&1 | Out-Null
    if ($LASTEXITCODE -ne 0) { & git remote set-url origin $url 2>&1 | Out-Null }
    $remote = (& git remote get-url origin 2>&1 | ForEach-Object { "$_" }) -join ''
    if ($LASTEXITCODE -ne 0) { Fail "Не удалось настроить origin: $remote" }
    Ok "origin -> $remote"
}
$remote = $remote.Trim()
if ($remote -notmatch 'github\.com[:/](?<repo>[^/]+/[^/]+?)(\.git)?/?$') { Fail "origin не похож на GitHub: $remote" }
$repo = $Matches.repo
$branch = ((& git rev-parse --abbrev-ref HEAD 2>&1) -join '').Trim()
if ($LASTEXITCODE -ne 0 -or $branch -eq 'HEAD') { $branch = 'main' }
Ok "Репозиторий $repo, ветка $branch"

# ── 2. Защита токенов ─────────────────────────────────────────
Step 'Проверяю, что секреты не попадут в git'
$tracked = @(git ls-files) | Where-Object { $_ -match '(^|/)\.env$' }
if ($tracked) { Fail ".env уже в репозитории ($($tracked -join ', ')). Удали: git rm --cached .env — и смени токен." }
$staged = @(git status --porcelain --untracked-files=all) | Where-Object { $_ -match '(^|/)\.env$' }
if ($staged) { Fail '.env не исключён .gitignore — проверь .gitignore.' }
Ok '.env не отслеживается'

# ── 3. Версия ────────────────────────────────────────────────
Step 'Версия'
git fetch --tags --quiet 2>&1 | Out-Null
$tags = @(@(git tag --list 'v*') | ForEach-Object { $_.TrimStart('v') } |
    Where-Object { $_ -match '^\d+\.\d+\.\d+$' } | ForEach-Object { [version]$_ } | Sort-Object)
$last = if ($tags.Count) { $tags[-1] } else { [version]'0.0.0' }

[xml]$xml = Get-Content -LiteralPath $csproj -Raw
$csVer = $xml.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
Write-Host "  Последний релиз: v$last   В Undertone.csproj: $csVer"

if (-not $Version) {
    $patch = "$($last.Major).$($last.Minor).$($last.Build + 1)"
    $minor = "$($last.Major).$($last.Minor + 1).0"
    $major = "$($last.Major + 1).0.0"
    $default = if ($csVer -and ([version]$csVer -gt $last)) { $csVer } else { $patch }
    Write-Host "  Варианты: $patch (фикс) · $minor (фичи) · $major (большое обновление)" -ForegroundColor DarkGray
    $Version = Read-Host "  Новая версия [Enter = $default]"
    if (-not $Version) { $Version = $default }
}
$Version = $Version.Trim().TrimStart('v')
if ($Version -notmatch '^\d+\.\d+\.\d+$') { Fail "Версия должна быть вида 1.2.3, а не '$Version'" }
if ([version]$Version -le $last) { Fail "v$Version не больше последнего релиза v$last — приложения не увидят обновление." }
if (@(git tag --list "v$Version").Count) { Fail "Тег v$Version уже существует." }
Ok "Публикую v$Version"

# ── 4. Пробная сборка ────────────────────────────────────────
Step 'Пробная сборка (чтобы не выпускать сломанное)'
if (Get-Command dotnet -ErrorAction SilentlyContinue) {
    & dotnet build $csproj -c Release --nologo -v quiet "-p:Version=$Version"
    if ($LASTEXITCODE -ne 0) { Fail 'Сборка упала — смотри ошибки выше. Ничего не опубликовано.' }
    Ok 'Собирается'
    if (Test-Path 'Undertone.Tests\Undertone.Tests.csproj') {
        & dotnet test 'Undertone.Tests\Undertone.Tests.csproj' -c Release --nologo -v quiet
        if ($LASTEXITCODE -ne 0) { Fail 'Тесты не прошли — смотри ошибки выше. Ничего не опубликовано.' }
        Ok 'Тесты прошли'
    }
} else {
    Warn 'dotnet не найден — пропускаю, проверит GitHub Actions.'
}

# ── 5. Версия в csproj ───────────────────────────────────────
$raw = Get-Content -LiteralPath $csproj -Raw
$new = [regex]::Replace($raw, '<Version>[^<]*</Version>', "<Version>$Version</Version>")
if ($new -ne $raw) {
    [IO.File]::WriteAllText((Resolve-Path $csproj).Path, $new, (New-Object System.Text.UTF8Encoding($false)))
    Ok "Undertone.csproj -> $Version"
}

# ── 6. Коммит ────────────────────────────────────────────────
Step 'Изменения'
git add -A
$changes = @(git status --porcelain)
if ($changes.Count) {
    $changes | Select-Object -First 30 | ForEach-Object { Write-Host "    $_" -ForegroundColor DarkGray }
    if ($changes.Count -gt 30) { Write-Host "    … и ещё $($changes.Count - 30)" -ForegroundColor DarkGray }
    if (-not $Message) {
        $Message = Read-Host "  Что нового в v${Version}? [Enter = Undertone $Version]"
        if (-not $Message) { $Message = "Undertone $Version" }
    }
} else {
    Warn 'Новых изменений нет — выпущу релиз из текущего коммита.'
}

$answer = Read-Host "`n  Публикуем v$Version в ${repo}? (y/n)"
if ($answer -notmatch '^(y|д|yes|да)$') {
    if ($changes.Count) { git reset --quiet }
    Fail 'Отменено. Ничего не отправлено.'
}

try {
    if ($changes.Count) { Invoke-Git commit -m $Message | Out-Null; Ok 'Коммит создан' }

    Step 'Отправляю на GitHub'
    Invoke-Git push origin $branch | Out-Null
    Ok "Код отправлен ($branch)"

    $tagMsg = if ($Message) { $Message } else { "Undertone $Version" }
    Invoke-Git tag -a "v$Version" -m $tagMsg | Out-Null
    Invoke-Git push origin "v$Version" | Out-Null
    Ok "Тег v$Version отправлен — сборка запущена"
} catch {
    Fail "Ошибка git: $($_.Exception.Message)"
}

# ── 7. Ждём GitHub Actions ───────────────────────────────────
Step 'Жду сборку в GitHub Actions (до 10 минут, можно закрыть окно)'
$headers = @{ 'User-Agent' = 'Undertone-Publish'; 'Accept' = 'application/vnd.github+json' }
$sha = (Invoke-Git rev-parse HEAD).Trim()
$deadline = (Get-Date).AddMinutes(10)
$run = $null
while ((Get-Date) -lt $deadline) {
    Start-Sleep -Seconds 10
    try {
        $runs = Invoke-RestMethod -Headers $headers -Uri "https://api.github.com/repos/$repo/actions/runs?head_sha=$sha&per_page=5"
        $run = $runs.workflow_runs | Where-Object { $_.head_branch -eq "v$Version" -or $_.event -eq 'push' } | Select-Object -First 1
    } catch { $run = $null }
    if ($run) {
        Write-Host "`r  Статус: $($run.status) $($run.conclusion)          " -NoNewline
        if ($run.status -eq 'completed') { break }
    } else {
        Write-Host "`r  Жду запуска сборки…" -NoNewline
    }
}
Write-Host ''

if ($run -and $run.status -eq 'completed' -and $run.conclusion -eq 'success') {
    Ok "Релиз v$Version опубликован!"
    Write-Host "  https://github.com/$repo/releases/tag/v$Version" -ForegroundColor White
    Write-Host '  Установленные копии предложат обновиться при запуске или в течение 6 часов.' -ForegroundColor DarkGray
} elseif ($run -and $run.status -eq 'completed') {
    Warn "Сборка завершилась с ошибкой ($($run.conclusion)). Лог: $($run.html_url)"
    Warn "Исправь, затем удали тег: git tag -d v$Version && git push origin :refs/tags/v$Version — и запусти скрипт снова."
} else {
    Warn 'Не дождался результата. Проверь вручную:'
    Write-Host "  https://github.com/$repo/actions" -ForegroundColor White
}

Write-Host ''
Read-Host '  Нажми Enter, чтобы закрыть' | Out-Null