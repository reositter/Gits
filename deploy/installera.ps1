<#
.SYNOPSIS
  Installerar eller uppdaterar inventeringstjänsten på Visma-servern.

.DESCRIPTION
  Körs som administratör från paketmappen (där mappen "app" ligger).
  Frågar efter mappar, portar och hur https ska ordnas, och skapar vid behov certifikat:
    1. Egen intern certifikatutfärdare (rekommenderas för internt nät)
    2. Självsignerat certifikat
    3. Befintligt certifikat (.pfx)
    4. Inget https
  Skriptet kan köras igen för att uppdatera programmet. Inställningar och data behålls.

.PARAMETER BaraCertifikat
  Byt bara certifikat/https-inställning för en befintlig installation.

.EXAMPLE
  .\installera.ps1
  .\installera.ps1 -BaraCertifikat
#>
[CmdletBinding()]
param(
    [switch]$BaraCertifikat,
    [string]$Installationsmapp = 'C:\Inventering',
    [string]$Datamapp = (Join-Path $env:ProgramData 'Inventering')
)

$ErrorActionPreference = 'Stop'
$TjanstNamn = 'Inventering'
$RotNamnPrefix = 'Inventering rotcertifikat'
$ServerNamnPrefix = 'Inventering server'

# ------------------------------------------------------------------ hjälpfunktioner

function Write-Rubrik([string]$Text) {
    Write-Host ''
    Write-Host $Text -ForegroundColor Cyan
    Write-Host ('-' * $Text.Length) -ForegroundColor DarkCyan
}

function Read-Svar([string]$Fraga, [string]$Standard) {
    $prompt = if ($Standard) { "$Fraga [$Standard]" } else { $Fraga }
    $svar = Read-Host $prompt
    if ([string]::IsNullOrWhiteSpace($svar)) { return $Standard }
    return $svar.Trim()
}

function Read-JaNej([string]$Fraga, [bool]$Standard = $true) {
    $std = if ($Standard) { 'J/n' } else { 'j/N' }
    while ($true) {
        $svar = Read-Host "$Fraga ($std)"
        if ([string]::IsNullOrWhiteSpace($svar)) { return $Standard }
        if ($svar -match '^(j|ja|y|yes)$') { return $true }
        if ($svar -match '^(n|nej|no)$') { return $false }
    }
}

function Read-Val([string]$Fraga, [string[]]$Alternativ, [int]$Standard = 1) {
    Write-Host $Fraga
    for ($i = 0; $i -lt $Alternativ.Count; $i++) { Write-Host ("  [{0}] {1}" -f ($i + 1), $Alternativ[$i]) }
    while ($true) {
        $svar = Read-Svar 'Välj' "$Standard"
        $n = 0
        if ([int]::TryParse($svar, [ref]$n) -and $n -ge 1 -and $n -le $Alternativ.Count) { return $n }
    }
}

function Test-PortLedig([int]$Port) {
    $upptagen = Get-NetTCPConnection -State Listen -LocalPort $Port -ErrorAction SilentlyContinue |
        Where-Object { $_.OwningProcess -ne 0 }
    if (-not $upptagen) { return $true }
    # Upptagen av vår egen tjänst räknas som ledig vid uppdatering.
    $egen = Get-CimInstance Win32_Service -Filter "Name='$TjanstNamn'" -ErrorAction SilentlyContinue
    return ($egen -and ($upptagen.OwningProcess -contains $egen.ProcessId))
}

function Get-ForeslagenPort([int]$Forsta, [int]$Andra) {
    if (Test-PortLedig $Forsta) { return $Forsta } else { return $Andra }
}

function Get-ServerNamn {
    $namn = New-Object System.Collections.Generic.List[string]
    $namn.Add($env:COMPUTERNAME.ToLowerInvariant())
    try {
        $fqdn = [System.Net.Dns]::GetHostEntry('').HostName.ToLowerInvariant()
        if ($fqdn -and -not $namn.Contains($fqdn)) { $namn.Add($fqdn) }
    } catch { }
    $ip = Get-NetIPAddress -AddressFamily IPv4 -ErrorAction SilentlyContinue |
        Where-Object { $_.IPAddress -notlike '127.*' -and $_.IPAddress -notlike '169.254.*' } |
        Select-Object -ExpandProperty IPAddress
    return @{ Namn = $namn.ToArray(); Ip = @($ip) }
}

# SAN-tillägg för New-SelfSignedCertificate: DNS-namn och IP-adresser.
function New-SanText([string[]]$Namn, [string[]]$Ip) {
    $delar = @()
    foreach ($n in $Namn) { if ($n) { $delar += "DNS=$n" } }
    foreach ($i in $Ip) { if ($i) { $delar += "IPAddress=$i" } }
    return '2.5.29.17={text}' + ($delar -join '&')
}

function Remove-GamlaServerCert {
    Get-ChildItem Cert:\LocalMachine\My | Where-Object { $_.FriendlyName -like "$ServerNamnPrefix*" } | ForEach-Object {
        Write-Host "  Tar bort tidigare servercertifikat $($_.Thumbprint)"
        Remove-Item $_.PSPath
    }
}

function New-InterntRotcert([string]$CertMapp) {
    $befintligt = Get-ChildItem Cert:\LocalMachine\My |
        Where-Object { $_.FriendlyName -like "$RotNamnPrefix*" -and $_.NotAfter -gt (Get-Date).AddYears(1) -and $_.HasPrivateKey } |
        Sort-Object NotAfter -Descending | Select-Object -First 1
    if ($befintligt -and (Read-JaNej "Det finns redan ett rotcertifikat (giltigt till $($befintligt.NotAfter.ToString('yyyy-MM-dd'))). Återanvända det? Annars måste alla telefoner installera ett nytt." $true)) {
        $rot = $befintligt
    } else {
        Write-Host '  Skapar rotcertifikat (giltigt i 10 år)…'
        $rot = New-SelfSignedCertificate -Type Custom `
            -Subject "CN=$RotNamnPrefix ($env:COMPUTERNAME), O=Inventering" `
            -FriendlyName "$RotNamnPrefix ($env:COMPUTERNAME)" `
            -KeyAlgorithm RSA -KeyLength 4096 -HashAlgorithm SHA256 `
            -KeyUsage CertSign, CRLSign, DigitalSignature -KeyUsageProperty Sign `
            -KeyExportPolicy NonExportable `
            -TextExtension @('2.5.29.19={critical}{text}ca=1&pathlength=0') `
            -NotAfter (Get-Date).AddYears(10) `
            -CertStoreLocation Cert:\LocalMachine\My
    }
    $cer = Join-Path $CertMapp 'inventering-rotcertifikat.cer'
    Export-Certificate -Cert $rot -FilePath $cer -Type CERT | Out-Null
    # Servern själv ska också lita på rotcertifikatet.
    Import-Certificate -FilePath $cer -CertStoreLocation Cert:\LocalMachine\Root | Out-Null
    return @{ Cert = $rot; Fil = $cer }
}

function New-ServerCert([string]$Huvudnamn, [string[]]$Namn, [string[]]$Ip, $Signerare) {
    Remove-GamlaServerCert
    # 825 dagar är den längsta giltighet som iPhone godtar för servercertifikat.
    $param = @{
        Type = 'Custom'
        Subject = "CN=$Huvudnamn"
        FriendlyName = "$ServerNamnPrefix ($Huvudnamn)"
        KeyAlgorithm = 'RSA'; KeyLength = 2048; HashAlgorithm = 'SHA256'
        KeyUsage = @('DigitalSignature', 'KeyEncipherment')
        KeyExportPolicy = 'NonExportable'
        TextExtension = @('2.5.29.37={text}1.3.6.1.5.5.7.3.1', (New-SanText $Namn $Ip))
        NotAfter = (Get-Date).AddDays(825)
        CertStoreLocation = 'Cert:\LocalMachine\My'
    }
    if ($Signerare) { $param.Signer = $Signerare }
    return New-SelfSignedCertificate @param
}

# Bygger inställningsfilen (appsettings.Production.json) som ett hashtable.
function New-Installningar($Befintliga, [string]$Data, [int]$HttpPort, [int]$HttpsPort, [hashtable]$Https) {
    $s = [ordered]@{}
    if ($Befintliga) { foreach ($p in $Befintliga.PSObject.Properties) { $s[$p.Name] = $p.Value } }
    $s['DataDirectory'] = $Data
    if (-not $s.Contains('Visma')) { $s['Visma'] = [ordered]@{ Mode = 'Adk' } }
    $endpoints = [ordered]@{ Http = [ordered]@{ Url = "http://0.0.0.0:$HttpPort" } }
    # Certifikatet väljs av tjänsten ur LocalMachine\My via tumavtryck (se Https nedan).
    if ($Https.Mode -ne 'None') { $endpoints['Https'] = [ordered]@{ Url = "https://0.0.0.0:$HttpsPort" } }
    $s['Kestrel'] = [ordered]@{ Endpoints = $endpoints }
    $s['Https'] = [ordered]@{
        Mode = $Https.Mode; PublicHost = $Https.PublicHost; Port = $HttpsPort
        RedirectHttp = ($Https.Mode -ne 'None'); RootCaFile = $Https.RootCaFile
        Thumbprint = $Https.Thumbprint; Subject = $Https.Subject
    }
    return $s
}

function Test-NetFramework48 {
    $rel = (Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full' -ErrorAction SilentlyContinue).Release
    return ($rel -ge 528040)
}

# Kör ADK-bryggan och listar bolagen som Visma känner till. Returnerar $null om ADK inte fungerar.
function Get-VismaBolag([string]$Brygga) {
    if (-not (Test-Path $Brygga)) { return $null }
    $psi = New-Object System.Diagnostics.ProcessStartInfo $Brygga
    $psi.RedirectStandardInput = $true; $psi.RedirectStandardOutput = $true; $psi.UseShellExecute = $false; $psi.CreateNoWindow = $true
    $psi.StandardOutputEncoding = [System.Text.Encoding]::UTF8
    $p = [System.Diagnostics.Process]::Start($psi)
    $p.StandardInput.Write('{"command":"companies"}'); $p.StandardInput.Close()
    $ut = $p.StandardOutput.ReadToEnd()
    if (-not $p.WaitForExit(60000)) { $p.Kill(); return $null }
    $svar = $ut | ConvertFrom-Json
    if ($p.ExitCode -ne 0) { Write-Warning "Visma-kopplingen svarade med fel: $($svar.error)"; return $null }
    return @($svar)
}

function Wait-Tjanst([int]$HttpPort) {
    for ($i = 0; $i -lt 30; $i++) {
        try {
            $r = Invoke-WebRequest "http://localhost:$HttpPort/api/health" -UseBasicParsing -TimeoutSec 3
            if ($r.StatusCode -eq 200) { return $true }
        } catch { }
        Start-Sleep -Seconds 1
    }
    return $false
}

# ------------------------------------------------------------------ huvudflöde

function Invoke-Installation {
    $principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'Kör skriptet som administratör (högerklicka på PowerShell och välj Kör som administratör).'
    }

    Write-Host ''
    Write-Host 'Inventering – installation' -ForegroundColor White
    Write-Host 'Tryck Enter för att välja förslaget inom [hakparentes].'

    $tjanst = Get-Service $TjanstNamn -ErrorAction SilentlyContinue
    $installerad = $null -ne $tjanst

    # ---- Mappar ----
    if ($installerad) {
        $sokvag = (Get-CimInstance Win32_Service -Filter "Name='$TjanstNamn'").PathName.Trim('"')
        $Installationsmapp = Split-Path $sokvag -Parent
        Write-Host "Befintlig installation hittad i $Installationsmapp."
    } elseif ($BaraCertifikat) {
        throw 'Ingen befintlig installation hittades. Kör utan -BaraCertifikat först.'
    }
    $instFil = Join-Path $Installationsmapp 'appsettings.Production.json'
    $befintliga = if (Test-Path $instFil) { Get-Content $instFil -Raw -Encoding UTF8 | ConvertFrom-Json } else { $null }
    if ($befintliga -and $befintliga.DataDirectory) { $Datamapp = $befintliga.DataDirectory }

    if (-not $BaraCertifikat) {
        Write-Rubrik 'Mappar'
        if (-not $installerad) { $Installationsmapp = Read-Svar 'Installationsmapp' $Installationsmapp }
        $Datamapp = Read-Svar 'Datamapp (databas, nycklar, säkerhetskopior)' $Datamapp
        $instFil = Join-Path $Installationsmapp 'appsettings.Production.json'
    }
    New-Item -ItemType Directory -Force -Path $Installationsmapp, $Datamapp | Out-Null
    $certMapp = Join-Path $Datamapp 'certifikat'
    New-Item -ItemType Directory -Force -Path $certMapp | Out-Null

    # ---- Portar ----
    Write-Rubrik 'Portar'
    $nuHttp = $null; $nuHttps = $null
    if ($befintliga -and $befintliga.Kestrel) {
        if ($befintliga.Kestrel.Endpoints.Http) { $nuHttp = ([uri]($befintliga.Kestrel.Endpoints.Http.Url -replace '0\.0\.0\.0', 'localhost')).Port }
        if ($befintliga.Kestrel.Endpoints.Https) { $nuHttps = ([uri]($befintliga.Kestrel.Endpoints.Https.Url -replace '0\.0\.0\.0', 'localhost')).Port }
    }
    if (-not $nuHttp) { $nuHttp = Get-ForeslagenPort 80 8080 }
    if (-not $nuHttps) { $nuHttps = Get-ForeslagenPort 443 8443 }
    $httpPort = [int](Read-Svar 'Port för http' $nuHttp)
    if (-not (Test-PortLedig $httpPort)) { Write-Warning "Port $httpPort används redan av ett annat program." }

    # ---- Certifikat ----
    Write-Rubrik 'Https och certifikat'
    Write-Host 'Mobilens kamera fungerar bara över https. Handskannrar och sökfältet fungerar även utan.'
    $val = Read-Val 'Hur ska https ordnas?' @(
        'Egen intern certifikatutfärdare – rekommenderas för internt nät. Telefonerna installerar ett rotcertifikat en gång, sedan inga varningar.',
        'Självsignerat certifikat – enklast, men webbläsaren varnar och iPhone glömmer ofta undantaget.',
        'Befintligt certifikat (.pfx) – t.ex. från Let''s Encrypt/win-acme eller företagets egen CA.',
        'Inget https – bara http.'
    ) 1

    $https = @{ Mode = 'None'; Subject = $null; PublicHost = $null; RootCaFile = $null; Thumbprint = $null }
    $httpsPort = $nuHttps
    if ($val -ne 4) {
        $httpsPort = [int](Read-Svar 'Port för https' $nuHttps)
        if (-not (Test-PortLedig $httpsPort)) { Write-Warning "Port $httpsPort används redan av ett annat program." }
    }

    if ($val -eq 1 -or $val -eq 2) {
        $server = Get-ServerNamn
        Write-Host ''
        Write-Host ('Serverns namn:  ' + ($server.Namn -join ', '))
        Write-Host ('IP-adresser:    ' + ($server.Ip -join ', '))
        $foreslaget = $server.Namn[$server.Namn.Count - 1]
        $huvud = Read-Svar 'Vilket namn ska användarna skriva i webbläsaren?' $foreslaget
        $extra = Read-Svar 'Fler namn att ta med, kommaseparerat (Enter = inga)' ''
        $alla = @($huvud) + $server.Namn + @($extra -split ',' | ForEach-Object { $_.Trim().ToLowerInvariant() } | Where-Object { $_ })
        $alla = $alla | Select-Object -Unique
        $ipLista = @($server.Ip)

        if ($val -eq 1) {
            $rot = New-InterntRotcert $certMapp
            Write-Host '  Skapar servercertifikat signerat av rotcertifikatet…'
            $cert = New-ServerCert $huvud $alla $ipLista $rot.Cert
            $https = @{ Mode = 'InternalCa'; Subject = $huvud; PublicHost = $huvud; RootCaFile = $rot.Fil; Thumbprint = $cert.Thumbprint }
        } else {
            Write-Host '  Skapar självsignerat certifikat…'
            $cert = New-ServerCert $huvud $alla $ipLista $null
            $cer = Join-Path $certMapp 'inventering-certifikat.cer'
            Export-Certificate -Cert $cert -FilePath $cer -Type CERT | Out-Null
            $https = @{ Mode = 'SelfSigned'; Subject = $huvud; PublicHost = $huvud; RootCaFile = $cer; Thumbprint = $cert.Thumbprint }
        }
        Write-Host "  Servercertifikat $($cert.Thumbprint) giltigt till $($cert.NotAfter.ToString('yyyy-MM-dd'))." -ForegroundColor Green
    } elseif ($val -eq 3) {
        $pfx = Read-Svar 'Sökväg till .pfx-filen' ''
        if (-not (Test-Path $pfx)) { throw "Filen $pfx finns inte." }
        $losen = Read-Host 'Lösenord för .pfx-filen' -AsSecureString
        Remove-GamlaServerCert
        $cert = Import-PfxCertificate -FilePath $pfx -Password $losen -CertStoreLocation Cert:\LocalMachine\My
        $cert = $cert | Where-Object { $_.HasPrivateKey } | Select-Object -First 1
        $cert.FriendlyName = "$ServerNamnPrefix (importerat)"
        $cn = ($cert.Subject -split ',')[0] -replace '^CN=', ''
        $huvud = Read-Svar 'Vilket namn ska användarna skriva i webbläsaren?' $cn
        $https = @{ Mode = 'Existing'; Subject = $cn; PublicHost = $huvud; RootCaFile = $null; Thumbprint = $cert.Thumbprint }
        Write-Host "  Importerat $($cert.Subject), giltigt till $($cert.NotAfter.ToString('yyyy-MM-dd'))." -ForegroundColor Green
        Write-Host '  Förnyas certifikatet med samma namn (t.ex. av win-acme direkt i certifikatarkivet) väljer tjänsten'
        Write-Host '  automatiskt det nyaste vid nästa omstart.'
    }

    # ---- Programfiler ----
    $stoppad = $false
    if ($installerad -and $tjanst.Status -eq 'Running') {
        Write-Host 'Stoppar tjänsten…'
        Stop-Service $TjanstNamn -Force
        $stoppad = $true
    }
    if (-not $BaraCertifikat) {
        Write-Rubrik 'Programfiler'
        $kalla = Join-Path $PSScriptRoot 'app'
        if (-not (Test-Path (Join-Path $kalla 'Inventering.Web.exe'))) { throw "Hittar inte programfilerna i $kalla. Kör skriptet från paketmappen." }
        # /MIR speglar mappen men behåller inställningsfilen.
        robocopy $kalla $Installationsmapp /MIR /XF appsettings.Production.json installera.ps1 'Byt certifikat.cmd' /NFL /NDL /NJH /NJS /NP | Out-Null
        if ($LASTEXITCODE -ge 8) { throw "Kopieringen misslyckades (robocopy $LASTEXITCODE)." }
        # Skripten följer med till installationsmappen så att certifikatet kan bytas senare.
        foreach ($f in 'installera.ps1', 'Byt certifikat.cmd') {
            $k = Join-Path $PSScriptRoot $f
            if ((Test-Path $k) -and ((Resolve-Path $k).Path -ne (Join-Path $Installationsmapp $f))) { Copy-Item $k $Installationsmapp -Force }
        }
        Write-Host "  Kopierat till $Installationsmapp"

        $brygga = Join-Path $Installationsmapp 'AdkBridge'
        if (-not (Test-Path (Join-Path $brygga 'AdkNet4Wrapper.dll'))) {
            Write-Host 'ADK-filerna AvSynkLib.dll och AdkNet4Wrapper.dll saknas i paketet.'
            $adk = Read-Svar 'Mapp där de finns (t.ex. _lib i ett befintligt ADK-projekt)' ''
            foreach ($f in 'AvSynkLib.dll', 'AdkNet4Wrapper.dll') {
                $k = Join-Path $adk $f
                if (-not (Test-Path $k)) { throw "Hittar inte $k." }
                Copy-Item $k $brygga -Force
            }
        }
    }

    # ---- Förutsättningar och Visma ----
    if (-not $BaraCertifikat) {
        Write-Rubrik 'Visma-kopplingen'
        if (-not (Test-NetFramework48)) {
            Write-Warning '.NET Framework 4.8 saknas. Det behövs för Visma-kopplingen (ADK). Installera det från Microsoft och kör skriptet igen.'
        } else {
            $bolag = Get-VismaBolag (Join-Path $Installationsmapp 'AdkBridge\Inventering.AdkBridge.exe')
            if ($null -eq $bolag) {
                Write-Warning 'Kunde inte nå Visma via ADK. Kontrollera att Visma Administration är installerat på servern. Bolag kan läggas till i admin senare.'
            } elseif ($bolag.Count -eq 0) {
                Write-Host 'Visma svarar, men inga företag hittades.'
            } else {
                Write-Host "Visma svarar. Hittade $($bolag.Count) företag:" -ForegroundColor Green
                $importFil = Join-Path $Datamapp 'bolag-att-importera.json'
                $valda = @()
                foreach ($b in $bolag) {
                    Write-Host ''
                    Write-Host ("  {0}  ({1})" -f $b.name, $b.directoryFullPath)
                    if (Read-JaNej '  Ta med bolaget?' $true) {
                        $namn = Read-Svar '  Namn som användarna ser' $b.name
                        $valda += [ordered]@{ Name = $namn; CompanyPath = $b.directoryFullPath; VismaName = $b.name }
                    }
                }
                if ($valda.Count) {
                    # Läses in av tjänsten vid start om inga bolag finns ännu.
                    [System.IO.File]::WriteAllText($importFil, (ConvertTo-Json @($valda) -Depth 5), (New-Object System.Text.UTF8Encoding($false)))
                    Write-Host "  $($valda.Count) bolag läggs upp när tjänsten startar."
                }
            }
        }
    }

    # ---- Inställningar ----
    $installningar = New-Installningar $befintliga $Datamapp $httpPort $httpsPort $https
    $json = $installningar | ConvertTo-Json -Depth 10
    [System.IO.File]::WriteAllText($instFil, $json, (New-Object System.Text.UTF8Encoding($false)))
    Write-Host "Inställningar sparade i $instFil"

    # ---- Tjänst ----
    if (-not $installerad) {
        Write-Rubrik 'Windows-tjänst'
        $exe = Join-Path $Installationsmapp 'Inventering.Web.exe'
        New-Service -Name $TjanstNamn -BinaryPathName "`"$exe`"" -DisplayName 'Inventering (Visma)' `
            -Description 'Lagerinventering med mobil och Excel mot Visma Administration.' -StartupType Automatic | Out-Null
        sc.exe failure $TjanstNamn reset= 86400 actions= restart/60000/restart/60000/restart/60000 | Out-Null
        Write-Host '  Tjänsten körs som LocalSystem. Ligger Visma-företagen på en nätverksdisk behöver den köras som ett'
        Write-Host '  konto med åtkomst dit (services.msc → Inventering → Logga in).'
    }

    # ---- Brandvägg (bara domän- och privat nät) ----
    $regel = 'Inventering (http/https)'
    Get-NetFirewallRule -DisplayName $regel -ErrorAction SilentlyContinue | Remove-NetFirewallRule
    $portar = @($httpPort)
    if ($https.Mode -ne 'None') { $portar += $httpsPort }
    New-NetFirewallRule -DisplayName $regel -Direction Inbound -Protocol TCP -LocalPort $portar -Action Allow -Profile Domain, Private | Out-Null

    Write-Host 'Startar tjänsten…'
    Start-Service $TjanstNamn
    $ok = Wait-Tjanst $httpPort

    # ---- Sammanfattning ----
    Write-Rubrik 'Klart'
    if (-not $ok) { Write-Warning "Tjänsten svarar inte ännu. Se Händelseloggen (Program) eller loggfilen, och kontrollera att porten $httpPort är ledig." }
    $vard = if ($https.PublicHost) { $https.PublicHost } else { $env:COMPUTERNAME.ToLowerInvariant() }
    $httpUrl = if ($httpPort -eq 80) { "http://$vard" } else { "http://$($vard):$httpPort" }
    $httpsUrl = if ($httpsPort -eq 443) { "https://$vard" } else { "https://$($vard):$httpsPort" }
    $bas = if ($https.Mode -eq 'None') { $httpUrl } else { $httpsUrl }
    Write-Host "Mobil:          $bas/"
    Write-Host "Administration: $bas/admin.html"
    if ($https.Mode -eq 'InternalCa' -or $https.Mode -eq 'SelfSigned') {
        Write-Host "Certifikat:     $httpUrl/certifikat.html" -ForegroundColor Yellow
        Write-Host '                Öppna den sidan på varje telefon (eller skanna QR-koden från en dator) och följ stegen.'
    }
    $forsta = Join-Path $Datamapp 'forsta-admin-losenord.txt'
    if (Test-Path $forsta) {
        $losen = (Get-Content $forsta -Encoding UTF8 | Where-Object { $_ -like 'Lösenord:*' }) -replace '^Lösenord:\s*', ''
        Write-Host ''
        Write-Host "Första inloggning: användare admin, lösenord $losen" -ForegroundColor Yellow
        Write-Host "Byt lösenordet efter inloggningen och radera filen $forsta."
    }
    if (-not (Test-Path (Join-Path $Datamapp 'bolag-att-importera.json.importerad'))) {
        Write-Host 'Lägg till bolag under Administration → Bolag → Sök bolag i Visma.'
    }
    Write-Host ''
    Write-Host "Certifikatet kan bytas senare under Administration → Server, eller med 'Byt certifikat.cmd' i $Installationsmapp."
    if ($ok -and (Read-JaNej 'Öppna administrationen i webbläsaren nu?' $true)) { Start-Process "$bas/admin.html" }
}

if (-not $env:INVENTERING_SKRIPTTEST) { Invoke-Installation }
