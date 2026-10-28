<#
.SYNOPSIS
    Inicia, encerra e acompanha o AWFace (frontend, API e PostgreSQL) com Docker Compose.

.DESCRIPTION
    Antes de subir os containers, o script prepara o arquivo .env:
      - cria o .env a partir do .env.example quando ele não existe;
      - acrescenta variáveis novas da versão atual sem alterar as existentes;
      - gera chaves fortes que estiverem vazias (dados pessoais e imagens de face);
      - preserva a chave antiga das imagens (1.0.x) em AWFACE_FACE_STORAGE_PREVIOUS_ENCRYPTION_KEY;
      - gera uma senha forte para o administrador inicial quando a atual é fraca;
      - sincroniza AWFACE_VERSION com o arquivo VERSION.
    Uma cópia de segurança do .env é criada antes de qualquer alteração.

.EXAMPLE
    .\scripts\awface.ps1 start
    .\scripts\awface.ps1 start -NoBuild
    .\scripts\awface.ps1 stop
    .\scripts\awface.ps1 restart
    .\scripts\awface.ps1 status
    .\scripts\awface.ps1 logs awface-api
    .\scripts\awface.ps1 check
    .\scripts\awface.ps1 retry-webhooks
#>
[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [ValidateSet('start', 'stop', 'restart', 'status', 'logs', 'check', 'retry-webhooks', 'help')]
    [string]$Command = 'help',

    [Parameter(Position = 1)]
    [string]$Service = '',

    # Sobe sem reconstruir as imagens (mais rápido quando o código não mudou).
    [switch]$NoBuild,

    # Tempo máximo, em segundos, para aguardar a API ficar saudável.
    [int]$TimeoutSeconds = 180
)

Set-StrictMode -Version 2.0
# 'Continue': no Windows PowerShell 5.1, mensagens do docker em stderr viram erro com 'Stop'.
# Os códigos de saída são verificados explicitamente.
$ErrorActionPreference = 'Continue'

$RepoRoot = Split-Path -Parent $PSScriptRoot
$EnvFile = Join-Path $RepoRoot '.env'
$EnvExample = Join-Path $RepoRoot '.env.example'
$VersionFile = Join-Path $RepoRoot 'VERSION'
$LegacyFaceKey = 'troque-esta-chave-em-producao'
$KnownPlaceholders = @($LegacyFaceKey, 'dev-only-change-this-face-storage-key', 'dev-only-change-this-data-key', 'change-me')
$KnownAdminPasswords = @('Awface@123', 'Intsis@1999', 'admin', '123456')
$Utf8NoBom = New-Object System.Text.UTF8Encoding $false

# ----------------------------------------------------------------------------- Saída

function Write-Step([string]$Message) { Write-Host "==> $Message" -ForegroundColor Cyan }
function Write-Ok([string]$Message) { Write-Host "    $Message" -ForegroundColor Green }
function Write-Info([string]$Message) { Write-Host "    $Message" }
function Write-Warn([string]$Message) { Write-Host "    AVISO: $Message" -ForegroundColor Yellow }
function Stop-WithError([string]$Message) {
    Write-Host ""
    Write-Host "ERRO: $Message" -ForegroundColor Red
    exit 1
}

# ----------------------------------------------------------------------------- Segredos

function New-RandomBytes([int]$Count) {
    $bytes = New-Object byte[] $Count
    $rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    try { $rng.GetBytes($bytes) } finally { $rng.Dispose() }
    return ,$bytes
}

function New-EncryptionKey {
    return [Convert]::ToBase64String((New-RandomBytes 32))
}

function New-Password {
    # 18 bytes -> 24 caracteres URL-safe, sem caracteres que confundam o .env.
    return ([Convert]::ToBase64String((New-RandomBytes 18))).Replace('+', '-').Replace('/', '_')
}

# Mesmo critério da API (KeyMaterial.IsWeak): vazia, de exemplo ou curta, salvo base64 de 32 bytes.
function Test-WeakKey([string]$Value) {
    if ([string]::IsNullOrWhiteSpace($Value)) { return $true }
    $trimmed = $Value.Trim()
    foreach ($placeholder in $KnownPlaceholders) {
        if ($trimmed -ieq $placeholder) { return $true }
    }
    try {
        if (([Convert]::FromBase64String($trimmed)).Length -eq 32) { return $false }
    } catch { }
    return ($trimmed.Length -lt 32)
}

function Test-WeakAdminPassword([string]$Value) {
    if ([string]::IsNullOrWhiteSpace($Value)) { return $true }
    if ($Value.Length -lt 12) { return $true }
    return ($KnownAdminPasswords -ccontains $Value)
}

# ----------------------------------------------------------------------------- Arquivo .env

function Read-EnvLines([string]$Path) {
    if (-not (Test-Path $Path)) { return @() }
    return @([System.IO.File]::ReadAllLines($Path))
}

function Get-EnvMap([string[]]$Lines) {
    $map = [ordered]@{}
    foreach ($line in $Lines) {
        if ($line -match '^\s*([A-Za-z_][A-Za-z0-9_]*)\s*=(.*)$') {
            $map[$Matches[1]] = $Matches[2].Trim()
        }
    }
    return $map
}

function Set-EnvLine([System.Collections.Generic.List[string]]$Lines, [string]$Key, [string]$Value) {
    $pattern = '^\s*' + [regex]::Escape($Key) + '\s*='
    for ($i = 0; $i -lt $Lines.Count; $i++) {
        if ($Lines[$i] -match $pattern) {
            $Lines[$i] = "$Key=$Value"
            return
        }
    }
    $Lines.Add("$Key=$Value")
}

function Remove-EnvLine([System.Collections.Generic.List[string]]$Lines, [string]$Key) {
    $pattern = '^\s*' + [regex]::Escape($Key) + '\s*='
    for ($i = $Lines.Count - 1; $i -ge 0; $i--) {
        if ($Lines[$i] -match $pattern) { $Lines.RemoveAt($i) }
    }
}

function Test-DirectoryHasFiles([string]$Path, [string]$Filter = '*') {
    if (-not (Test-Path $Path)) { return $false }
    return $null -ne (Get-ChildItem -Path $Path -Recurse -File -Filter $Filter -ErrorAction SilentlyContinue | Select-Object -First 1)
}

function Initialize-EnvFile {
    Write-Step 'Preparando o arquivo .env'

    if (-not (Test-Path $EnvExample)) { Stop-WithError ".env.example não encontrado em $RepoRoot." }

    $created = $false
    if (-not (Test-Path $EnvFile)) {
        Copy-Item $EnvExample $EnvFile -ErrorAction Stop
        $created = $true
        Write-Ok '.env criado a partir do .env.example.'
    }

    $original = Read-EnvLines $EnvFile
    $lines = New-Object 'System.Collections.Generic.List[string]'
    foreach ($line in $original) { $lines.Add($line) }
    $current = Get-EnvMap $original
    $example = Get-EnvMap (Read-EnvLines $EnvExample)
    $notes = New-Object 'System.Collections.Generic.List[string]'
    $adminPasswordToShow = $null

    # 1. Variáveis novas da versão atual (sem sobrescrever as existentes).
    $added = @()
    foreach ($key in $example.Keys) {
        if (-not $current.Contains($key)) {
            Set-EnvLine $lines $key $example[$key]
            $current[$key] = $example[$key]
            $added += $key
        }
    }
    if ($added.Count -gt 0 -and -not $created) {
        $notes.Add("Variáveis novas adicionadas: $($added -join ', ').")
    }

    # 2. Variável obsoleta.
    if ($current.Contains('AWFACE_HOMOLOGATION_ACCEPT_NON_PROCESSED_RESULT')) {
        Remove-EnvLine $lines 'AWFACE_HOMOLOGATION_ACCEPT_NON_PROCESSED_RESULT'
        $notes.Add('Removida a variável obsoleta AWFACE_HOMOLOGATION_ACCEPT_NON_PROCESSED_RESULT.')
    }

    # 3. Versão.
    if (Test-Path $VersionFile) {
        $version = ([System.IO.File]::ReadAllText($VersionFile)).Trim()
        if ($current['AWFACE_VERSION'] -ne $version) {
            Set-EnvLine $lines 'AWFACE_VERSION' $version
            $notes.Add("AWFACE_VERSION sincronizada com VERSION ($version).")
        }
    }

    # 4. Senha do Postgres: só é gerada se o volume do banco ainda não existe.
    if ([string]::IsNullOrWhiteSpace($current['POSTGRES_PASSWORD'])) {
        if (Test-DirectoryHasFiles (Join-Path $RepoRoot 'docker-data\postgres-awface')) {
            Stop-WithError ('POSTGRES_PASSWORD está vazia, mas o banco já existe em docker-data\postgres-awface. ' +
                'Informe no .env a senha usada na criação do banco.')
        }
        Set-EnvLine $lines 'POSTGRES_PASSWORD' (New-Password)
        $notes.Add('POSTGRES_PASSWORD gerada.')
    } elseif ($current['POSTGRES_PASSWORD'].Length -lt 12) {
        Write-Warn 'POSTGRES_PASSWORD é curta. Troque-a no banco (ALTER USER) e no .env após os testes.'
    }

    # 5. Chave mestra dos dados pessoais.
    $dataKey = $current['AWFACE_DATA_ENCRYPTION_KEY']
    if ([string]::IsNullOrWhiteSpace($dataKey)) {
        Set-EnvLine $lines 'AWFACE_DATA_ENCRYPTION_KEY' (New-EncryptionKey)
        $notes.Add('AWFACE_DATA_ENCRYPTION_KEY gerada. Guarde-a em local seguro: sem ela os dados pessoais não podem ser lidos.')
    } elseif (Test-WeakKey $dataKey) {
        Stop-WithError ('AWFACE_DATA_ENCRYPTION_KEY é fraca ou de exemplo. Se nenhum dado foi gravado com ela, ' +
            'apague o valor no .env para que uma nova chave seja gerada.')
    }

    # 6. Chave das imagens de face, preservando a chave padrão da 1.0.x para leitura.
    $faceKey = $current['AWFACE_FACE_STORAGE_ENCRYPTION_KEY']
    $previousFaceKey = $current['AWFACE_FACE_STORAGE_PREVIOUS_ENCRYPTION_KEY']
    if (Test-WeakKey $faceKey) {
        $oldKey = $faceKey
        if ([string]::IsNullOrWhiteSpace($oldKey)) { $oldKey = $LegacyFaceKey }
        $hasFaces = Test-DirectoryHasFiles (Join-Path $RepoRoot 'docker-data\awface-storage') '*.enc'
        if ($hasFaces -and [string]::IsNullOrWhiteSpace($previousFaceKey)) {
            Set-EnvLine $lines 'AWFACE_FACE_STORAGE_PREVIOUS_ENCRYPTION_KEY' $oldKey
            $notes.Add('Chave anterior das imagens de face preservada em AWFACE_FACE_STORAGE_PREVIOUS_ENCRYPTION_KEY.')
        }
        Set-EnvLine $lines 'AWFACE_FACE_STORAGE_ENCRYPTION_KEY' (New-EncryptionKey)
        $notes.Add('AWFACE_FACE_STORAGE_ENCRYPTION_KEY gerada.')
    }

    # 7. Administrador inicial.
    if ([string]::IsNullOrWhiteSpace($current['AWFACE_ADMIN_EMAIL'])) {
        Set-EnvLine $lines 'AWFACE_ADMIN_EMAIL' 'admin@awface.local'
        $current['AWFACE_ADMIN_EMAIL'] = 'admin@awface.local'
        $notes.Add('AWFACE_ADMIN_EMAIL definido como admin@awface.local.')
    }
    if (Test-WeakAdminPassword $current['AWFACE_ADMIN_PASSWORD']) {
        $adminPasswordToShow = New-Password
        Set-EnvLine $lines 'AWFACE_ADMIN_PASSWORD' $adminPasswordToShow
        $notes.Add('AWFACE_ADMIN_PASSWORD fraca ou vazia: nova senha gerada.')
    }

    # Grava somente se algo mudou, com backup.
    $changed = $created -or ($original.Count -ne $lines.Count)
    if (-not $changed) {
        for ($i = 0; $i -lt $lines.Count; $i++) {
            if ($original[$i] -cne $lines[$i]) { $changed = $true; break }
        }
    }

    if ($changed) {
        if (-not $created) {
            $backup = "$EnvFile.bak-$(Get-Date -Format 'yyyyMMddHHmmss')"
            Copy-Item $EnvFile $backup -ErrorAction Stop
            Write-Info "Cópia de segurança: $(Split-Path -Leaf $backup)"
        }
        [System.IO.File]::WriteAllLines($EnvFile, $lines.ToArray(), $Utf8NoBom)
    }

    foreach ($note in $notes) { Write-Ok $note }
    if (-not $changed) { Write-Ok '.env já está pronto.' }

    if ($adminPasswordToShow) {
        Write-Host ''
        Write-Host '    Acesso administrativo inicial (válido apenas se ainda não houver administrador no banco):' -ForegroundColor Yellow
        Write-Host "      E-mail: $($current['AWFACE_ADMIN_EMAIL'])" -ForegroundColor Yellow
        Write-Host "      Senha:  $adminPasswordToShow" -ForegroundColor Yellow
        Write-Host '    A senha também está no .env (AWFACE_ADMIN_PASSWORD). Remova-a de lá após o primeiro acesso.' -ForegroundColor Yellow
        Write-Host ''
    }
}

# ----------------------------------------------------------------------------- Docker

$script:ComposeCommand = $null

function Resolve-Compose {
    if (-not (Get-Command docker -ErrorAction SilentlyContinue)) {
        Stop-WithError 'Docker não encontrado. Instale e abra o Docker Desktop.'
    }

    & docker info *> $null
    if ($LASTEXITCODE -ne 0) {
        Stop-WithError 'O Docker não está respondendo. Abra o Docker Desktop e aguarde ele ficar pronto.'
    }

    & docker compose version *> $null
    if ($LASTEXITCODE -eq 0) {
        $script:ComposeCommand = @('docker', 'compose')
        return
    }

    if (Get-Command docker-compose -ErrorAction SilentlyContinue) {
        $script:ComposeCommand = @('docker-compose')
        return
    }

    Stop-WithError 'Docker Compose não encontrado (docker compose ou docker-compose).'
}

function Invoke-Compose([string[]]$Arguments) {
    $exe = $script:ComposeCommand[0]
    $prefix = @()
    if ($script:ComposeCommand.Count -gt 1) { $prefix = $script:ComposeCommand[1..($script:ComposeCommand.Count - 1)] }

    Push-Location $RepoRoot
    try {
        & $exe @prefix @Arguments | Out-Host
        return $LASTEXITCODE
    } finally {
        Pop-Location
    }
}

function Set-BuildMetadata {
    $commit = 'unknown'
    if (Get-Command git -ErrorAction SilentlyContinue) {
        Push-Location $RepoRoot
        try {
            $value = (& git rev-parse --short HEAD 2>$null)
            if ($LASTEXITCODE -eq 0 -and $value) { $commit = $value.Trim() }
        } finally {
            Pop-Location
        }
    }

    # Variáveis do processo têm prioridade sobre o .env no Docker Compose.
    $env:AWFACE_BUILD_COMMIT = $commit
    $env:AWFACE_BUILD_DATE = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')
    if (Test-Path $VersionFile) {
        $env:AWFACE_VERSION = ([System.IO.File]::ReadAllText($VersionFile)).Trim()
    }
}

function Get-FrontPort {
    $map = Get-EnvMap (Read-EnvLines $EnvFile)
    if ($map.Contains('AWFACE_FRONT_PORT') -and $map['AWFACE_FRONT_PORT']) { return $map['AWFACE_FRONT_PORT'] }
    return '5580'
}

function Get-Health([string]$Url) {
    try {
        $response = Invoke-WebRequest -Uri $Url -UseBasicParsing -TimeoutSec 5
        if ($response.StatusCode -eq 200) { return $response.Content }
    } catch { }
    return $null
}

function Test-ApiExited {
    $status = (& docker inspect -f '{{.State.Status}}' awface-api 2>$null)
    if ($LASTEXITCODE -ne 0 -or -not $status) { return $false }
    return (@('exited', 'restarting', 'dead') -contains ([string]$status).Trim())
}

function Wait-ForHealth {
    $port = Get-FrontPort
    $url = "http://localhost:$port/health"
    Write-Step "Aguardando a API ficar disponível ($url)"

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    $startedAt = Get-Date
    while ((Get-Date) -lt $deadline) {
        $health = Get-Health $url
        if ($health) {
            Write-Ok "API saudável: $health"
            return $true
        }

        # Falha de inicialização (ex.: chave inválida) derruba o container: mostra o motivo sem esperar o timeout.
        if (((Get-Date) - $startedAt).TotalSeconds -gt 15 -and (Test-ApiExited)) {
            Write-Warn 'O container awface-api parou durante a inicialização. Últimas linhas do log:'
            Invoke-Compose @('logs', '--tail', '40', 'awface-api') | Out-Null
            return $false
        }

        Start-Sleep -Seconds 3
        Write-Host '.' -NoNewline
    }

    Write-Host ''
    Write-Warn "A API não respondeu em $TimeoutSeconds segundos. Veja: .\scripts\awface.ps1 logs awface-api"
    return $false
}

function Show-Urls {
    $port = Get-FrontPort
    Write-Host ''
    Write-Host 'AWFace em execução:' -ForegroundColor Green
    Write-Host "  Admin (tenants):    http://localhost:$port/#/admin"
    Write-Host "  Jornada assistida:  http://localhost:$port/#/journey-start"
    Write-Host "  Health da API:      http://localhost:$port/health"
    $apiPort = (Get-EnvMap (Read-EnvLines $EnvFile))['AWFACE_API_PORT']
    if (-not $apiPort) { $apiPort = '5500' }
    Write-Host "  API direta:         http://127.0.0.1:$apiPort/health"
    $dbPort = (Get-EnvMap (Read-EnvLines $EnvFile))['POSTGRES_PORT']
    if (-not $dbPort) { $dbPort = '5432' }
    Write-Host "  PostgreSQL:         127.0.0.1:$dbPort"
    Write-Host ''
    Write-Host 'Para encerrar: .\scripts\awface.ps1 stop'
}

# ----------------------------------------------------------------------------- Comandos

# ----------------------------------------------------------------------------- Portas

function Test-PortAvailable([string]$Address, [int]$Port) {
    $ip = [System.Net.IPAddress]::Any
    if ($Address -and $Address -ne '0.0.0.0') { $ip = [System.Net.IPAddress]::Parse($Address) }
    $listener = New-Object System.Net.Sockets.TcpListener($ip, $Port)
    try {
        $listener.ExclusiveAddressUse = $true
        $listener.Start()
        return $true
    } catch {
        return $false
    } finally {
        try { $listener.Stop() } catch { }
    }
}

function Get-PortOwner([int]$Port) {
    if (-not (Get-Command Get-NetTCPConnection -ErrorAction SilentlyContinue)) { return $null }
    try {
        $connection = Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction Stop | Select-Object -First 1
        $process = Get-Process -Id $connection.OwningProcess -ErrorAction Stop
        return "$($process.ProcessName) (PID $($process.Id))"
    } catch {
        return $null
    }
}

function Test-ContainerRunning([string]$Name) {
    $running = (& docker inspect -f '{{.State.Running}}' $Name 2>$null)
    return ($LASTEXITCODE -eq 0 -and ([string]$running).Trim() -eq 'true')
}

function Find-FreePort([string]$Address, [int]$From, [int]$To) {
    for ($port = $From; $port -le $To; $port++) {
        if (Test-PortAvailable $Address $port) { return $port }
    }
    return $null
}

function Get-PortBlockReason([int]$Port) {
    $owner = Get-PortOwner $Port
    if ($owner) { return "em uso por $owner" }
    return 'reservada pelo Windows (Hyper-V/WinNAT) ou em uso por outro serviço'
}

# Verifica as portas publicadas no host antes do docker compose up.
# Banco e API são acessados pelos outros containers pela rede interna, então trocar a porta
# publicada no host não afeta a aplicação: nesses casos o script escolhe uma porta livre.
function Resolve-HostPorts {
    Write-Step 'Verificando portas do host'

    $map = Get-EnvMap (Read-EnvLines $EnvFile)
    $updates = [ordered]@{}

    $ports = @(
        @{ Name = 'Banco (PostgreSQL)'; Container = 'awface-db';    PortKey = 'POSTGRES_PORT';     Default = 5432; BindKey = 'POSTGRES_BIND';   DefaultBind = '127.0.0.1'; Range = @(5433, 5499); Auto = $true },
        @{ Name = 'API';                Container = 'awface-api';   PortKey = 'AWFACE_API_PORT';   Default = 5500; BindKey = 'AWFACE_API_BIND'; DefaultBind = '127.0.0.1'; Range = @(5501, 5579); Auto = $true },
        @{ Name = 'Frontend';           Container = 'awface-front'; PortKey = 'AWFACE_FRONT_PORT'; Default = 5580; BindKey = $null;             DefaultBind = '0.0.0.0';   Range = $null;          Auto = $false }
    )

    foreach ($item in $ports) {
        $port = $item.Default
        if ($map.Contains($item.PortKey) -and $map[$item.PortKey]) { $port = [int]$map[$item.PortKey] }
        $bind = $item.DefaultBind
        if ($item.BindKey -and $map.Contains($item.BindKey) -and $map[$item.BindKey]) { $bind = $map[$item.BindKey] }

        if (Test-ContainerRunning $item.Container) {
            Write-Ok "$($item.Name): porta $port (container já em execução)."
            continue
        }

        if (Test-PortAvailable $bind $port) {
            Write-Ok "$($item.Name): porta $port livre."
            continue
        }

        $reason = Get-PortBlockReason $port
        if (-not $item.Auto) {
            Stop-WithError ("A porta $port do $($item.Name) está $reason. Libere a porta ou altere " +
                "AWFACE_FRONT_PORT e AWFACE_FRONTEND_BASE_URL no .env (ex.: 5581 e http://localhost:5581).")
        }

        $newPort = Find-FreePort $bind $item.Range[0] $item.Range[1]
        if (-not $newPort) {
            Stop-WithError "A porta $port do $($item.Name) está $reason e não há porta livre entre $($item.Range[0]) e $($item.Range[1])."
        }

        Write-Warn "$($item.Name): porta $port $reason. Usando a porta $newPort no host (gravada em $($item.PortKey) no .env)."
        $updates[$item.PortKey] = [string]$newPort
    }

    if ($updates.Count -gt 0) {
        $lines = New-Object 'System.Collections.Generic.List[string]'
        foreach ($line in (Read-EnvLines $EnvFile)) { $lines.Add($line) }
        foreach ($key in $updates.Keys) { Set-EnvLine $lines $key $updates[$key] }
        [System.IO.File]::WriteAllLines($EnvFile, $lines.ToArray(), $Utf8NoBom)
    }
}

function Start-AWFace {
    Resolve-Compose
    Initialize-EnvFile
    Resolve-HostPorts
    Set-BuildMetadata

    $arguments = @('up', '-d')
    if (-not $NoBuild) {
        $arguments += '--build'
        Write-Step 'Construindo as imagens e subindo os containers (a primeira vez pode levar alguns minutos)'
    } else {
        Write-Step 'Subindo os containers'
    }

    if ((Invoke-Compose $arguments) -ne 0) {
        Stop-WithError 'docker compose up falhou. Verifique as mensagens acima.'
    }

    if (Wait-ForHealth) {
        Show-Urls
    } else {
        exit 1
    }
}

function Stop-AWFace {
    Resolve-Compose
    Write-Step 'Encerrando os containers (dados do banco, imagens e logs são preservados em docker-data)'
    if ((Invoke-Compose @('down')) -ne 0) {
        Stop-WithError 'docker compose down falhou.'
    }
    Write-Ok 'AWFace encerrado.'
}

function Show-Status {
    Resolve-Compose
    Write-Step 'Containers'
    Invoke-Compose @('ps') | Out-Null
    $port = Get-FrontPort
    $health = Get-Health "http://localhost:$port/health"
    if ($health) { Write-Ok "API saudável: $health" } else { Write-Warn 'API não está respondendo.' }
}

function Show-Logs {
    Resolve-Compose
    $arguments = @('logs', '-f', '--tail', '200')
    if ($Service) { $arguments += $Service }
    Invoke-Compose $arguments | Out-Null
}

function Test-Configuration {
    Resolve-Compose
    Initialize-EnvFile
    Resolve-HostPorts
    Set-BuildMetadata
    Write-Step 'Validando o docker-compose.yml com o .env'
    if ((Invoke-Compose @('config', '--quiet')) -ne 0) {
        Stop-WithError 'Configuração do Docker Compose inválida.'
    }
    Write-Ok 'Configuração válida.'
}

function Invoke-RetryWebhooks {
    Resolve-Compose
    if (-not (Test-ContainerRunning 'awface-db')) {
        Stop-WithError 'O container awface-db não está em execução. Rode .\scripts\awface.ps1 start primeiro.'
    }

    $map = Get-EnvMap (Read-EnvLines $EnvFile)
    $user = 'postgres'
    $database = 'awface'
    if ($map.Contains('POSTGRES_USER') -and $map['POSTGRES_USER']) { $user = $map['POSTGRES_USER'] }
    if ($map.Contains('POSTGRES_DB') -and $map['POSTGRES_DB']) { $database = $map['POSTGRES_DB'] }

    Write-Step 'Reagendando webhooks não entregues para envio imediato'
    $sql = "update awface_callback_outbox set status = 'PENDING', next_attempt_at = now(), " +
        "attempts = least(attempts, 9), locked_until = null, updated_at = now() " +
        "where status in ('PENDING', 'FAILED') and delivered_at is null;"
    & docker exec awface-db psql -U $user -d $database -v ON_ERROR_STOP=1 -c $sql | Out-Host
    if ($LASTEXITCODE -ne 0) { Stop-WithError 'Não foi possível reagendar os webhooks.' }

    Write-Ok 'Webhooks reagendados. Acompanhe com: .\scripts\awface.ps1 logs awface-api'
}

function Show-Help {
    Write-Host @'
Uso: .\scripts\awface.ps1 <comando> [serviço] [-NoBuild] [-TimeoutSeconds 180]

Comandos:
  start     Prepara o .env, constrói as imagens e sobe frontend, API e banco.
            -NoBuild  sobe sem reconstruir as imagens.
  stop      Encerra os containers (os dados em docker-data são preservados).
  restart   Executa stop e start.
  status    Mostra os containers e o health da API.
  logs      Acompanha os logs (opcional: awface-api, awface-front ou awface-db).
  check     Prepara o .env e valida a configuração sem subir nada.
  retry-webhooks
            Reenvia agora os webhooks ainda não entregues ao Tenant.

Pelo Prompt de Comando ou duplo clique, use awface.cmd na raiz do projeto.
'@
}

switch ($Command) {
    'start'   { Start-AWFace }
    'stop'    { Stop-AWFace }
    'restart' { Stop-AWFace; Start-AWFace }
    'status'  { Show-Status }
    'logs'    { Show-Logs }
    'check'   { Test-Configuration }
    'retry-webhooks' { Invoke-RetryWebhooks }
    default   { Show-Help }
}
