# Publishing helpers. Dot-source this file; it does not publish anything by itself.
function Get-ReleaseCredential([bool]$Login) {
    $oldPrompt = $env:GIT_TERMINAL_PROMPT
    $oldInteractive = $env:GCM_INTERACTIVE
    $credentialLines = $null
    try {
        $env:GIT_TERMINAL_PROMPT = '0'
        $request = "protocol=https`nhost=github.com`npath=ZhangShiQi/cut.git`n`n"
        $attempts = @(@{ Path = 'true'; Interactive = 'never' }, @{ Path = 'false'; Interactive = 'never' })
        if ($Login) { $attempts += @{ Path = 'true'; Interactive = 'always' } }
        foreach ($attempt in $attempts) {
            $env:GCM_INTERACTIVE = $attempt.Interactive
            try { $credentialLines = @($request | & git -c ("credential.useHttpPath=" + $attempt.Path) credential fill 2>$null) }
            catch { continue }
            if ($LASTEXITCODE -ne 0) { continue }
            $password = $credentialLines | Where-Object { $_.StartsWith('password=') } | Select-Object -First 1
            if ($password -and $password.Length -gt 9) { return $password.Substring(9).TrimEnd("`r") }
        }
        throw 'GitHub credentials unavailable. Retry release.ps1 -Login to sign in through Git Credential Manager.'
    }
    finally {
        $credentialLines = $null
        $env:GIT_TERMINAL_PROMPT = $oldPrompt
        $env:GCM_INTERACTIVE = $oldInteractive
    }
}

function Invoke-ReleaseApi {
    param([string]$Method = 'GET', [string]$Url, [string]$Token, [object]$Body,
        [string]$FilePath, [switch]$AllowNotFound)
    $uri = [uri]$Url
    if ($uri.Scheme -ne 'https' -or $uri.Host -notin @('api.github.com','uploads.github.com') -or
        !($uri.AbsolutePath -eq '/repos/ZhangShiQi/cut' -or $uri.AbsolutePath.StartsWith('/repos/ZhangShiQi/cut/')) -or $uri.UserInfo -or !$uri.IsDefaultPort) {
        throw 'Release request is outside the official repository.'
    }
    $parameters = @{ Uri = $Url; Method = $Method; ErrorAction = 'Stop'; MaximumRedirection = 0; Headers = @{
        Authorization = 'Bearer ' + $Token; 'User-Agent' = 'Cut-local-publisher'; Accept = 'application/vnd.github+json'; 'Cache-Control' = 'no-cache' } }
    if ($FilePath) { $parameters.InFile = $FilePath; $parameters.ContentType = 'application/octet-stream' }
    elseif ($null -ne $Body) {
        $parameters.Body = [Text.Encoding]::UTF8.GetBytes(($Body | ConvertTo-Json -Depth 8 -Compress))
        $parameters.ContentType = 'application/json; charset=utf-8'
    }
    try { return Invoke-RestMethod @parameters }
    catch {
        $status = if ($_.Exception.Response) { [int]$_.Exception.Response.StatusCode } else { 0 }
        if ($AllowNotFound -and $status -eq 404) { return $null }
        # Do not print request headers, credentials or response bodies in errors.
        throw "GitHub release request failed (HTTP $status): $Method $($uri.AbsolutePath). Check connectivity and repository write permissions."
    }
}

function Get-ReleaseRepository([string]$Token) {
    $repository = Invoke-ReleaseApi -Url 'https://api.github.com/repos/ZhangShiQi/cut' -Token $Token
    if ($repository.full_name -ne 'ZhangShiQi/cut') { throw 'GitHub returned an unexpected publishing repository.' }
    return $repository
}

function Get-ReleaseState([string]$Tag, [string]$Token) {
    $api = 'https://api.github.com/repos/ZhangShiQi/cut'
    $published = Invoke-ReleaseApi -Url "$api/releases/tags/$Tag" -Token $Token -AllowNotFound
    if ($published) { return $published }
    # GitHub's by-tag endpoint may omit unpublished drafts. Writers can find them via the list API.
    for ($page = 1; $page -le 50; $page++) {
        $releases = @(Invoke-ReleaseApi -Url "$api/releases?per_page=100&page=$page" -Token $Token)
        $matches = @($releases | Where-Object { $_.tag_name -eq $Tag })
        if ($matches.Count -gt 1) { throw 'Multiple releases use this tag. Inspect drafts before retrying.' }
        if ($matches.Count -eq 1) { return $matches[0] }
        if ($releases.Count -lt 100) { return $null }
    }
    throw 'Release lookup exceeded the pagination limit.'
}

function Send-LocalRelease {
    param([string]$Tag, [string]$Version, [string]$Commit, [string]$Package, [string]$Notes, [string]$Token)
    $api = 'https://api.github.com/repos/ZhangShiQi/cut'
    $release = Get-ReleaseState -Tag $Tag -Token $Token
    if ($release -and !$release.draft) { throw 'This Release is already published; it will not be overwritten.' }
    if (!$release) {
        $release = Invoke-ReleaseApi -Method POST -Url "$api/releases" -Token $Token -Body @{
            tag_name = $Tag; target_commitish = $Commit; name = "Cut $Version"; body = $Notes; draft = $true; prerelease = $false }
    }
    foreach ($path in @($Package, ($Package + '.sha256'))) {
        # Only replace assets belonging to this unpublished draft; retries never change a published Release.
        $release = Invoke-ReleaseApi -Url "$api/releases/$($release.id)" -Token $Token
        if (!$release -or !$release.draft) { throw 'The draft changed while uploading. Stop and inspect the Release before retrying.' }
        $name = [IO.Path]::GetFileName($path)
        foreach ($asset in @($release.assets | Where-Object { $_.name -eq $name })) {
            Invoke-ReleaseApi -Method DELETE -Url "$api/releases/assets/$($asset.id)" -Token $Token | Out-Null
        }
        $url = "https://uploads.github.com/repos/ZhangShiQi/cut/releases/$($release.id)/assets?name=" + [uri]::EscapeDataString($name)
        $uploaded = Invoke-ReleaseApi -Method POST -Url $url -Token $Token -FilePath $path
        $hash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($uploaded.digest -ne ('sha256:' + $hash)) { throw 'GitHub asset digest does not match the local file. Release remains a draft.' }
    }
    $release = Invoke-ReleaseApi -Url "$api/releases/$($release.id)" -Token $Token
    if (!$release -or !$release.draft) { throw 'The Release was changed by another publisher. No publish request was sent.' }
    Invoke-ReleaseApi -Method PATCH -Url "$api/releases/$($release.id)" -Token $Token -Body @{
        name = "Cut $Version"; body = $Notes; draft = $false; prerelease = $false; make_latest = 'true' } | Out-Null
}
