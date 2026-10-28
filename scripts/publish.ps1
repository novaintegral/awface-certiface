<#
.SYNOPSIS
    Constrói as imagens Docker do AWFace (frontend e API) e publica no Docker Hub.

.DESCRIPTION
    Usa a conta conectada no Docker Desktop (docker login). As imagens recebem a versão do
    arquivo VERSION como tag e os metadados de release (commit e data) como labels OCI:

        <namespace>/awface-front:<versão>
        <namespace>/awface-api:<versão>

    Proteções:
      - recusa publicar com alterações não commitadas (use -AllowDirty para testes);
      - valida a consistência de versões (VERSION, package.json, release-version.ts);
      - recusa sobrescrever uma versão que já existe no Docker Hub (use -Force);
      - a tag 'latest' só é atualizada com -Latest.

.EXAMPLE
    .\scripts\publish.ps1                    # build + push da versão do arquivo VERSION
    .\scripts\publish.ps1 -Latest            # também atualiza a tag latest
    .\scripts\publish.ps1 -DryRun            # mostra o que seria feito, sem build nem push
    .\scripts\publish.ps1 -Only api          # somente a imagem da API
    .\scripts\publish.ps1 -Tag 1.1.0-rc.1    # tag explícita (pré-release)
    .\scripts\publish.ps1 -Namespace minhaconta
#>
[CmdletBinding()]
param(
    # Usuário ou organização do Docker Hub (prefixo das imagens).
    [string]$Namespace = 'novaintegral',

    # Tag das imagens. Padrão: conteúdo do arquivo VERSION.
    [string]$Tag = '',

    # Quais imagens construir e publicar.
    [ValidateSet('all', 'front', 'api')]
    [string]$Only = 'all',

    # Plataforma das imagens (servidores on-premise Linux x64).
    [string]$Platform = 'linux/amd64',

    # Também publica a tag 'latest'.
    [switch]$Latest,

    # Publica mesmo que a tag já exista no Docker Hub.
    [switch]$Force,

    # Permite publicar com alterações não commitadas (as imagens ficam marcadas com '-dirty').
    [switch]$AllowDirty,

    # Reaproveita imagens já construídas localmente com a mesma tag (somente push).
    [switch]$SkipBuild,

    # Constrói sem usar o cache do Docker.
    [switch]$NoCache,

    # Apenas mostra o plano, sem build nem push.
    [switch]$DryRun
)

Set-StrictMode -Version 2.0
# 'Continue': no Windows PowerShell 5.1, mensagens do docker em stderr viram erro com 'Stop'.
# Os códigos de saída são verificados explicitamente.
$ErrorActionPreference = 'Continue'

$RepoRoot = Split-Path -Parent $PSScriptRoot
$VersionFile = Join-Path $RepoRoot 'VERSION'
$SemVerPattern = '^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?(?:\+[0-9A-Za-z.-]+)?$'

function Write-Step([string]$Message) { Write-Host "==> $Message" -ForegroundColor Cyan }
function Write-Ok([string]$Message) { Write-Host "    $Message" -ForegroundColor Green }
function Write-Info([string]$Message) { Write-Host "    $Message" }
function Write-Warn([string]$Message) { Write-Host "    AVISO: $Message" -ForegroundColor Yellow }
function Stop-WithError([string]$Message) {
    Write-Host ''
    Write-Host "ERRO: $Message" -ForegroundColor Red
    exit 1
}

function Invoke-Native([string]$Exe, [string[]]$Arguments) {
    Write-Host "    > $Exe $($Arguments -join ' ')" -ForegroundColor DarkGray
    if ($DryRun) { return 0 }
    Push-Location $RepoRoot
    try {
        & $Exe @Arguments | Out-Host
        return $LASTEXITCODE
    } finally {
        Pop-Location
    }
}

# ----------------------------------------------------------------------------- Pré-requisitos

Write-Step 'Verificando o Docker'
if (-not (Get-Command docker -ErrorAction SilentlyContinue)) {
    Stop-WithError 'Docker não encontrado. Instale e abra o Docker Desktop.'
}
& docker info *> $null
if ($LASTEXITCODE -ne 0) {
    Stop-WithError 'O Docker não está respondendo. Abra o Docker Desktop e aguarde ele ficar pronto.'
}

$dockerUser = (& docker info --format '{{.Username}}' 2>$null)
if ($LASTEXITCODE -eq 0 -and $dockerUser -and ([string]$dockerUser).Trim()) {
    $dockerUser = ([string]$dockerUser).Trim()
    Write-Ok "Docker Hub conectado como '$dockerUser'."
    if ($dockerUser -ne $Namespace) {
        Write-Info "As imagens serão publicadas em '$Namespace'; a conta '$dockerUser' precisa ter permissão de escrita nesse namespace."
    }
} else {
    # Versões recentes do Docker Desktop guardam a sessão no credential helper e não expõem o usuário.
    Write-Warn "Não foi possível confirmar o usuário do Docker Hub. Se o push falhar com 'denied', entre na conta pelo Docker Desktop ou rode 'docker login'."
}

# ----------------------------------------------------------------------------- Versão e commit

Write-Step 'Verificando versão e commit'
if (-not (Test-Path $VersionFile)) { Stop-WithError "Arquivo VERSION não encontrado em $RepoRoot." }
$version = ([System.IO.File]::ReadAllText($VersionFile)).Trim()
if (-not $Tag) { $Tag = $version }
$Tag = $Tag.TrimStart('v')
if ($Tag -notmatch $SemVerPattern) { Stop-WithError "Tag inválida '$Tag'. Use SemVer, por exemplo 1.2.3 ou 1.2.3-rc.1." }
if ($Tag.Split('-')[0].Split('+')[0] -ne $version.Split('-')[0].Split('+')[0]) {
    Write-Warn "A tag '$Tag' difere da versão do arquivo VERSION ($version)."
}

if (Get-Command node -ErrorAction SilentlyContinue) {
    Push-Location $RepoRoot
    try {
        & node scripts/verify-version.mjs | Out-Host
        if ($LASTEXITCODE -ne 0) { Stop-WithError 'Versões divergentes. Rode: npm run version:sync' }
    } finally {
        Pop-Location
    }
} else {
    Write-Warn 'Node.js não encontrado; a consistência de versões será validada apenas no build do frontend.'
}

$commit = 'unknown'
$branch = ''
if (Get-Command git -ErrorAction SilentlyContinue) {
    Push-Location $RepoRoot
    try {
        $commit = ([string](& git rev-parse --short HEAD 2>$null)).Trim()
        $branch = ([string](& git rev-parse --abbrev-ref HEAD 2>$null)).Trim()
        $dirty = (& git -c core.autocrlf=true status --porcelain 2>$null)
        if ($dirty) {
            if (-not $AllowDirty) {
                Write-Host ($dirty | Out-String)
                Stop-WithError 'Há alterações não commitadas. Faça commit antes de publicar (ou use -AllowDirty para uma imagem de teste).'
            }
            $commit = "$commit-dirty"
            Write-Warn 'Publicando com alterações não commitadas (commit marcado como -dirty).'
        }
    } finally {
        Pop-Location
    }
}
$buildDate = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')
Write-Ok "Versão $Tag · commit $commit · branch $branch"

# ----------------------------------------------------------------------------- Imagens

$images = @(
    @{ Key = 'front'; Name = "$Namespace/awface-front"; Dockerfile = 'Dockerfile' },
    @{ Key = 'api';   Name = "$Namespace/awface-api";   Dockerfile = 'backend/AWFace.Api/Dockerfile' }
) | Where-Object { $Only -eq 'all' -or $_.Key -eq $Only }

Write-Step 'Verificando tags no Docker Hub'
foreach ($image in $images) {
    $reference = "$($image.Name):$Tag"
    & docker manifest inspect $reference *> $null
    if ($LASTEXITCODE -eq 0) {
        if (-not $Force) {
            Stop-WithError "$reference já existe no Docker Hub. Versões publicadas não devem ser sobrescritas: gere uma nova versão (npm run release:prepare -- patch) ou use -Force."
        }
        Write-Warn "$reference já existe e será sobrescrita (-Force)."
    } else {
        Write-Ok "$reference ainda não publicada."
    }
}

if (-not $SkipBuild) {
    foreach ($image in $images) {
        Write-Step "Construindo $($image.Name):$Tag"
        $arguments = @(
            'build',
            '--file', $image.Dockerfile,
            '--platform', $Platform,
            '--build-arg', "AWFACE_VERSION=$version",
            '--build-arg', "AWFACE_BUILD_COMMIT=$commit",
            '--build-arg', "AWFACE_BUILD_DATE=$buildDate",
            '--label', "org.opencontainers.image.version=$Tag",
            '--label', "org.opencontainers.image.revision=$commit",
            '--label', "org.opencontainers.image.created=$buildDate",
            '--tag', "$($image.Name):$Tag"
        )
        if ($Latest) { $arguments += @('--tag', "$($image.Name):latest") }
        if ($NoCache) { $arguments += '--no-cache' }
        $arguments += '.'

        if ((Invoke-Native 'docker' $arguments) -ne 0) {
            Stop-WithError "Falha no build de $($image.Name). Nada foi publicado."
        }
    }
} else {
    Write-Step 'Pulando o build (-SkipBuild)'
    foreach ($image in $images) {
        & docker image inspect "$($image.Name):$Tag" *> $null
        if ($LASTEXITCODE -ne 0 -and -not $DryRun) {
            Stop-WithError "Imagem local $($image.Name):$Tag não encontrada. Rode sem -SkipBuild."
        }
        if ($Latest -and -not $DryRun) {
            & docker tag "$($image.Name):$Tag" "$($image.Name):latest"
        }
    }
}

# Push somente depois de todos os builds terem dado certo, para não publicar um par incompleto.
foreach ($image in $images) {
    $tags = @($Tag)
    if ($Latest) { $tags += 'latest' }
    foreach ($currentTag in $tags) {
        Write-Step "Publicando $($image.Name):$currentTag"
        if ((Invoke-Native 'docker' @('push', "$($image.Name):$currentTag")) -ne 0) {
            Stop-WithError ("Falha no push de $($image.Name):$currentTag. Verifique o login no Docker Desktop " +
                "e se a conta tem permissão de escrita em '$Namespace'.")
        }
    }
}

# ----------------------------------------------------------------------------- Resumo

Write-Host ''
if ($DryRun) {
    Write-Host 'Simulação concluída (-DryRun): nada foi construído nem publicado.' -ForegroundColor Yellow
    exit 0
}

Write-Host 'Publicação concluída:' -ForegroundColor Green
foreach ($image in $images) {
    $digest = (& docker image inspect --format '{{range .RepoDigests}}{{println .}}{{end}}' "$($image.Name):$Tag" 2>$null) |
        Where-Object { $_ -like "$($image.Name)@*" } | Select-Object -First 1
    Write-Host "  $($image.Name):$Tag"
    if ($digest) { Write-Host "    $digest" -ForegroundColor DarkGray }
}
Write-Host ''
Write-Host 'No servidor, defina AWFACE_VERSION no .env e rode:'
Write-Host "  docker compose -f deploy/compose-awface.yml pull"
Write-Host "  docker compose -f deploy/compose-awface.yml up -d"
