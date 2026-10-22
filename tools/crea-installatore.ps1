<#
.SYNOPSIS
    Costruisce il pacchetto di installazione di BlindPhone.

.DESCRIPTION
    Fa due cose in sequenza: pubblica l'applicazione in una cartella e ne ricava
    un pacchetto .msi. Il risultato finisce in dist\.

    PREREQUISITI
      - SDK .NET 10
      - WiX 5, con le estensioni per l'interfaccia e per le azioni ausiliarie:

            dotnet tool install --global wix --version 5.*
            wix extension add -g WixToolset.UI.wixext/5.0.2
            wix extension add -g WixToolset.Util.wixext/5.0.2

.PARAMETER Versione
    Versione del pacchetto, nella forma X.Y.Z. Se omessa si legge da
    Directory.Build.props, cosi' che i binari e il pacchetto non possano
    divergere.

.PARAMETER Architettura
    Architettura del processore della postazione: x64 (predefinita) oppure arm64.

    Sono due pacchetti distinti e non intercambiabili, e vanno distribuiti come
    tali: il nome del file porta l'architettura apposta. Un pacchetto x64 si
    installa anche su Windows on ARM, dove l'emulazione lo fa funzionare, ma paga
    l'emulazione su un programma che deve stare dietro all'audio in tempo reale.
    Il contrario non vale affatto: un pacchetto arm64 su una postazione x64 non
    parte.

.PARAMETER Autonomo
    Se vero (predefinito) il pacchetto porta dentro di se' il motore di
    esecuzione .NET e non ha prerequisiti. Se falso produce un pacchetto molto
    piu' piccolo, che pero' pretende .NET Desktop Runtime 10 gia' installato sulla
    postazione: ha senso solo dove il motore di esecuzione e' gestito
    centralmente.

.PARAMETER SaltaPubblicazione
    Riusa la cartella di pubblicazione esistente. Utile per provare modifiche al
    solo file .wxs senza ricompilare l'applicazione. La cartella riusata e' quella
    dell'architettura richiesta, non l'ultima costruita: le due stanno in cartelle
    separate proprio perche' non si possano scambiare.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools\crea-installatore.ps1

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools\crea-installatore.ps1 -Versione 1.0.0

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools\crea-installatore.ps1 -Architettura arm64
#>

[CmdletBinding()]
param(
    [string] $Versione,
    [ValidateSet('x64', 'arm64')]
    [string] $Architettura = 'x64',
    [bool] $Autonomo = $true,
    [switch] $SaltaPubblicazione
)

$ErrorActionPreference = 'Stop'

$radice = $PSScriptRoot
if ([string]::IsNullOrEmpty($radice)) { $radice = Split-Path -Parent $PSCommandPath }
if ([string]::IsNullOrEmpty($radice)) { $radice = (Get-Location).Path }
$radice = Split-Path -Parent $radice

$progetto = Join-Path $radice 'src\BlindPhone.App\BlindPhone.App.csproj'
$sorgenteWix = Join-Path $radice 'installer\BlindPhone.wxs'
$cartellaGrafica = Join-Path $radice 'installer'
$fileIcona = Join-Path $radice 'src\BlindPhone.App\blindphone.ico'
$cartellaDist = Join-Path $radice 'dist'

# Una cartella di pubblicazione per architettura, e non una sola riusata a turno.
# Con una cartella unica, -SaltaPubblicazione dopo una compilazione x64 avrebbe
# infilato binari x64 dentro un pacchetto dichiarato arm64: un pacchetto che si
# installa senza un errore e poi non parte, cioe' il guasto piu' costoso da capire
# fra quelli possibili qui.
$cartellaPubblicazione = Join-Path $radice "artifacts\pubblicazione-$Architettura"

# ---------------------------------------------------------------------------
# Versione
# ---------------------------------------------------------------------------

# La versione sta in un posto solo. Se la si ripetesse qui, prima o poi il pacchetto
# direbbe una versione e i binari un'altra, e la segnalazione di un guasto diventerebbe
# impossibile da collocare.
$props = Join-Path $radice 'Directory.Build.props'
$xml = [xml] (Get-Content $props -Raw)

if ([string]::IsNullOrWhiteSpace($Versione)) {
    $Versione = ($xml.Project.PropertyGroup.Version | Where-Object { $_ }) | Select-Object -First 1
}

# L'etichetta e' la versione come la legge una persona: e' quella che porta la parola
# "beta", che l'installatore di Windows non accetterebbe nel numero di versione.
# Compare nel nome del prodotto e nell'elenco delle app installate.
$etichetta = ($xml.Project.PropertyGroup.InformationalVersion | Where-Object { $_ }) | Select-Object -First 1
if ([string]::IsNullOrWhiteSpace($etichetta)) { $etichetta = $Versione }

if ($Versione -notmatch '^\d+\.\d+\.\d+$') {
    throw "Versione non valida: '$Versione'. L'installatore di Windows accetta la sola forma X.Y.Z."
}

Write-Host "BlindPhone $etichetta (versione del pacchetto $Versione)" -ForegroundColor Cyan
Write-Host "Architettura $Architettura"
Write-Host ("Pacchetto {0}" -f $(if ($Autonomo) { 'autonomo (include .NET)' } else { 'dipendente da .NET Desktop Runtime 10' }))

# ---------------------------------------------------------------------------
# Verifica dei prerequisiti, prima di lavorare per due minuti a vuoto
# ---------------------------------------------------------------------------

$wix = Get-Command wix -ErrorAction SilentlyContinue
if (-not $wix) {
    throw "WiX non trovato. Installalo con: dotnet tool install --global wix --version 5.*"
}

$estensioni = (& wix extension list -g) -join "`n"
foreach ($richiesta in @('WixToolset.UI.wixext', 'WixToolset.Util.wixext')) {
    if ($estensioni -notmatch [regex]::Escape($richiesta)) {
        throw "Estensione WiX mancante. Installala con: wix extension add -g $richiesta/5.0.2"
    }
}

foreach ($file in @($sorgenteWix, $fileIcona,
                    (Join-Path $cartellaGrafica 'striscia.bmp'),
                    (Join-Path $cartellaGrafica 'riquadro.bmp'))) {
    if (-not (Test-Path $file)) {
        throw "File mancante: $file. Se e' grafica, rigenerala con tools\genera-icona.ps1"
    }
}

# ---------------------------------------------------------------------------
# Pubblicazione
# ---------------------------------------------------------------------------

if ($SaltaPubblicazione) {
    if (-not (Test-Path (Join-Path $cartellaPubblicazione 'BlindPhone.App.exe'))) {
        throw "Nessuna pubblicazione da riusare in $cartellaPubblicazione"
    }

    Write-Host "`nPubblicazione riusata: $cartellaPubblicazione"
}
else {
    Write-Host "`nPubblicazione in corso..."

    # Si parte da una cartella vuota: i file rimasti da una pubblicazione
    # precedente finirebbero dentro al pacchetto senza che nessuno se ne accorga.
    if (Test-Path $cartellaPubblicazione) {
        Remove-Item $cartellaPubblicazione -Recurse -Force
    }

    $argomenti = @(
        'publish', $progetto,
        '-c', 'Release',
        '-r', "win-$Architettura",
        "--self-contained=$($Autonomo.ToString().ToLower())",
        '-o', $cartellaPubblicazione,
        '--nologo', '-v', 'q'
    )

    & dotnet @argomenti
    if ($LASTEXITCODE -ne 0) { throw "Pubblicazione non riuscita (codice $LASTEXITCODE)" }

    # La documentazione dei tipi non va installata: serve a chi scrive codice, non
    # al programma. Si riconosce dal fatto che accanto al .xml c'e' un assembly con
    # lo stesso nome — cosi' un eventuale .xml di configurazione resta al suo posto.
    #
    # I .pdb invece restano, e non e' una svista: senza, l'eccezione registrata da
    # una postazione arriva con una traccia di indirizzi al posto dei nomi dei
    # metodi, cioe' illeggibile proprio quando serve.
    $documentazione = Get-ChildItem $cartellaPubblicazione -Filter *.xml | Where-Object {
        $base = Join-Path $cartellaPubblicazione ([System.IO.Path]::GetFileNameWithoutExtension($_.Name))
        (Test-Path "$base.dll") -or (Test-Path "$base.exe")
    }

    if ($documentazione) {
        $documentazione | Remove-Item -Force
        Write-Host "  rimossi $($documentazione.Count) file di documentazione"
    }
}

$fileP = Get-ChildItem $cartellaPubblicazione -Recurse -File
Write-Host ("  {0} file, {1:N1} MB" -f $fileP.Count, (($fileP | Measure-Object Length -Sum).Sum / 1MB))

# ---------------------------------------------------------------------------
# Nessun dato personale nel pacchetto
# ---------------------------------------------------------------------------
#
# Il pacchetto viene distribuito ad altri enti. La cartella dei dati
# dell'operatore (%APPDATA%\BlindPhone) non e' dichiarata nel file .wxs e non
# viene mai pubblicata, quindi in condizioni normali qui non arriva nulla di
# personale. Ma "in condizioni normali" non e' una garanzia: basta una copia fatta
# a mano dentro artifacts\, o un domani un file di configurazione aggiunto al
# progetto come contenuto, e il pacchetto porterebbe in giro l'account SIP e la
# rubrica di chi lo ha costruito.
#
# Il controllo e' bloccante di proposito. Un pacchetto gia' firmato e distribuito
# non si richiama indietro, e i dati di traffico telefonico sono dati personali:
# meglio una compilazione fermata che una segnalazione al Garante.

$nomiVietati = @(
    'settings.json',            # account SIP, indirizzo del centralino, dispositivi
    'secrets.dat',              # password SIP e Client Secret, cifrati ma personali
    'hotkeys.json',             # profili di tasti di un operatore specifico
    'contacts.db',              # la rubrica
    'contacts.db-wal',
    'contacts.db-shm',
    'history.db',               # dati di traffico: chi ha chiamato chi, e quando
    'history.db-wal',
    'history.db-shm',
    'certificati-fidati.json'   # quali certificati questa postazione ha approvato
)

# .zip e' l'estensione del pacchetto per l'assistenza, che contiene registri e
# rapporti di errore. .bpcfg e' quella del pacchetto di configurazione, che contiene
# account SIP, indirizzo del centralino e criterio della postazione su cui e' stato
# preparato: chi lo distribuisce lo passa all'installatore con la proprieta'
# CONFIGURAZIONE, che lo copia in ProgramData senza farlo entrare nel prodotto.
$estensioniVietate = @('.log', '.wav', '.csv', '.bak', '.zip', '.bpcfg')

# Le cartelle dei dati dell'operatore non devono comparire nella pubblicazione, e i
# nomi dei file che contengono non bastano a intercettarle: un rapporto di errore si
# chiama "errore-20260807-221530-1.txt", cioe' un nome che nessun elenco puo'
# prevedere.
$cartelleVietate = @(
    'errori',                   # rapporti di errore: tracce di stack, percorsi, utenza
    'log'                       # registri: numeri chiamati e chiamanti riga per riga
)

$sospetti = $fileP | Where-Object {
    ($nomiVietati -contains $_.Name) -or
    ($estensioniVietate -contains $_.Extension.ToLowerInvariant()) -or
    ($_.Name -like 'errore-*.txt')
}

# Il controllo sulle cartelle si fa in un passaggio separato e non dentro il filtro
# qui sopra: annidando un Where-Object dentro l'altro la variabile automatica si
# ombreggia, e il risultato e' un controllo che non controlla piu' nulla continuando a
# rispondere "nessun dato personale". E' il modo peggiore in cui una guardia puo'
# rompersi, perche' il messaggio rassicurante resta.
$percorsoPubblicazione = (Resolve-Path $cartellaPubblicazione).Path

$sospettiCartella = $fileP | Where-Object {
    $relativo = $_.FullName.Substring($percorsoPubblicazione.Length).TrimStart('\', '/')
    $primo = ($relativo -split '[\\/]')[0]
    $cartelleVietate -contains $primo.ToLowerInvariant()
}

$sospetti = @($sospetti) + @($sospettiCartella) | Sort-Object FullName -Unique

if ($sospetti) {
    $elenco = ($sospetti | ForEach-Object { "  " + $_.FullName }) -join "`n"
    throw "Dati personali nella cartella di pubblicazione: il pacchetto non verra' costruito.`n$elenco"
}

Write-Host "  nessun dato personale nella pubblicazione (verificati nomi, estensioni e cartelle a rischio)"

# ---------------------------------------------------------------------------
# Pacchetto
# ---------------------------------------------------------------------------

if (-not (Test-Path $cartellaDist)) { New-Item -ItemType Directory -Path $cartellaDist -Force | Out-Null }

$suffisso = if ($Autonomo) { '' } else { '-richiede-dotnet' }
$pacchetto = Join-Path $cartellaDist "BlindPhone-$Versione-$Architettura$suffisso.msi"

Write-Host "`nCostruzione del pacchetto..."

$argomentiWix = @(
    'build', $sorgenteWix,
    '-arch', $Architettura,
    '-culture', 'it-IT',
    '-ext', 'WixToolset.UI.wixext',
    '-ext', 'WixToolset.Util.wixext',
    '-d', "Versione=$Versione",
    '-d', "EtichettaVersione=$etichetta",
    '-d', "CartellaPubblicazione=$cartellaPubblicazione",
    '-d', "CartellaGrafica=$cartellaGrafica",
    '-d', "FileIcona=$fileIcona",
    '-o', $pacchetto
)

& wix @argomentiWix
if ($LASTEXITCODE -ne 0) { throw "Costruzione del pacchetto non riuscita (codice $LASTEXITCODE)" }

$dimensione = (Get-Item $pacchetto).Length

Write-Host "`nFatto." -ForegroundColor Green
Write-Host ("  {0}" -f $pacchetto)
Write-Host ("  {0:N1} MB" -f ($dimensione / 1MB))
Write-Host ""
Write-Host "Installazione senza interazione:  msiexec /i `"$pacchetto`" /qn"
Write-Host "Senza collegamento sul desktop:   msiexec /i `"$pacchetto`" /qn DESKTOP=0"
