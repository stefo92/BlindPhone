<#
.SYNOPSIS
    Sostituisce recapiti telefonici e nominativi nei registri diagnostici di
    BlindPhone, lasciando il resto intatto.

.DESCRIPTION
    I registri di un posto operatore contengono dati personali: chi ha chiamato,
    da quale numero, a che ora e con chi ha parlato. Sono dati di traffico
    telefonico ai sensi del GDPR, e non devono uscire dall'ente per finire in
    un'analisi diagnostica.

    Lo script produce una copia in cui ogni recapito e' sostituito da
    un'etichetta stabile: lo stesso numero diventa sempre la stessa etichetta,
    dentro tutti i file elaborati nella stessa esecuzione. Senza quella
    stabilita' un registro non si potrebbe piu' leggere - seguire una chiamata
    significa riconoscere lo stesso recapito in venti righe diverse.

    NON SOSTITUISCE ALLA CIECA OGNI SEQUENZA DI CIFRE.

    Un registro SIP e' pieno di numeri che non sono recapiti: porte (5061),
    identificativi di ramo, CSeq, SSRC, conteggi di pacchetti, millisecondi.
    Sostituirli renderebbe il registro inutile proprio per la diagnosi a cui
    serve. Si interviene percio' nelle sole posizioni in cui un numero e' un
    recapito: la parte utente di un URI SIP, i nomi visualizzati fra virgolette
    e le frasi del programma che annunciano un chiamante.

.PARAMETER Registro
    File di registro, oppure cartella che li contiene. Se omesso prende la
    cartella dei registri di BlindPhone.

.PARAMETER Destinazione
    Cartella in cui scrivere le copie anonime. Se omessa, una sottocartella
    "anonimo" accanto ai registri.

.PARAMETER Mappa
    Percorso del file in cui scrivere la corrispondenza fra etichette e recapiti
    veri. RESTA SULLA TUA MACCHINA: serve a te per risalire a chi era
    "ESTERNO-03" quando l'analisi te lo nomina. Se omesso non viene scritto.

.PARAMETER ConservaNomi
    Lascia intatti i nomi visualizzati. Da usare solo se sei certo che non
    contengano nominativi di persone.

.PARAMETER Tutto
    Rifa' anche i registri gia' anonimizzati e non piu' modificati. Senza questo
    parametro vengono saltati, perche' rifarli produce gli stessi file e costa
    minuti. Serve quando cambiano le regole di anonimizzazione: le copie vecchie
    sono state prodotte con le regole vecchie.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools\anonimizza-registro.ps1

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools\anonimizza-registro.ps1 `
        -Registro "$env:APPDATA\BlindPhone\log\blindphone-20260811.log" `
        -Mappa "$env:USERPROFILE\Desktop\mappa-recapiti.txt"
#>

[CmdletBinding()]
param(
    [string] $Registro,
    [string] $Destinazione,
    [string] $Mappa,
    [switch] $ConservaNomi,
    [switch] $Tutto
)

$ErrorActionPreference = 'Stop'

# ---------------------------------------------------------------------------
# Individuazione dei file
# ---------------------------------------------------------------------------

if ([string]::IsNullOrWhiteSpace($Registro)) {
    $Registro = Join-Path $env:APPDATA 'BlindPhone\log'
}

if (-not (Test-Path $Registro)) {
    throw "Non trovo '$Registro'."
}

$origine = Get-Item $Registro

if ($origine.PSIsContainer) {
    $file = Get-ChildItem -Path $origine.FullName -Filter '*.log' -File
    $cartellaOrigine = $origine.FullName
}
else {
    $file = @($origine)
    $cartellaOrigine = $origine.DirectoryName
}

if ($file.Count -eq 0) {
    throw "Nessun file di registro in '$Registro'."
}

if ([string]::IsNullOrWhiteSpace($Destinazione)) {
    $Destinazione = Join-Path $cartellaOrigine 'anonimo'
}

if (-not (Test-Path $Destinazione)) {
    New-Item -ItemType Directory -Path $Destinazione -Force | Out-Null
}

# La copia non deve mai finire sopra l'originale: e' l'unica prova di cosa e'
# successo davvero, e una sovrascrittura la distruggerebbe senza rimedio.
if ((Resolve-Path $Destinazione).Path -eq (Resolve-Path $cartellaOrigine).Path) {
    throw "La cartella di destinazione coincide con quella dei registri originali. Scegline un'altra."
}

# ---------------------------------------------------------------------------
# Registro delle sostituzioni
# ---------------------------------------------------------------------------

$recapiti = [ordered]@{}
$nomi = [ordered]@{}

function Get-EtichettaRecapito {
    param([string] $Valore)

    $chiave = ($Valore -replace '[^\d+]', '')
    if ([string]::IsNullOrWhiteSpace($chiave)) { return $Valore }

    if ($recapiti.Contains($chiave)) { return $recapiti[$chiave] }

    # Interno o esterno si distinguono dalla lunghezza, e la distinzione va
    # conservata: leggendo il registro cambia tutto sapere se chi chiama e'
    # dentro la struttura o fuori.
    $cifre = ($chiave -replace '\D', '')
    $tipo = if ($cifre.Length -le 5) { 'INTERNO' } else { 'ESTERNO' }

    $progressivo = 1 + (@($recapiti.Values | Where-Object { $_ -like "$tipo-*" }).Count)
    $etichetta = '{0}-{1:D2}' -f $tipo, $progressivo

    $recapiti[$chiave] = $etichetta
    return $etichetta
}

function Get-EtichettaNome {
    param([string] $Valore)

    if ($nomi.Contains($Valore)) { return $nomi[$Valore] }

    $etichetta = 'NOMINATIVO-{0:D2}' -f ($nomi.Count + 1)
    $nomi[$Valore] = $etichetta
    return $etichetta
}

# Etichette che il centralino usa per le linee e non per le persone: non sono
# dati personali, e sostituirle toglierebbe contesto senza proteggere nessuno.
$etichetteGeneriche = @(
    'Chiamate esterne',
    'Centralino-Chiamate-Interne',
    'Anonymous',
    'unknown',
    'Restricted'
)

function Convert-NomeVisualizzato {
    param([string] $Contenuto)

    # Il centralino compone i nomi come "Etichetta:Dettaglio", dove il dettaglio
    # e' tanto un numero quanto il nominativo di una persona.
    $pezzi = $Contenuto -split ':'

    $tradotti = foreach ($pezzo in $pezzi) {
        $pulito = $pezzo.Trim()

        if ([string]::IsNullOrWhiteSpace($pulito)) { $pezzo }
        elseif ($etichetteGeneriche -contains $pulito) { $pezzo }
        elseif ($pulito -match '^\+?[\d\s\-\.]{3,}$') { Get-EtichettaRecapito $pulito }
        elseif ($ConservaNomi) { $pezzo }
        else { Get-EtichettaNome $pulito }
    }

    return ($tradotti -join ':')
}

# ---------------------------------------------------------------------------
# Regole di sostituzione
# ---------------------------------------------------------------------------

function Convert-Riga {
    param([string] $Riga)

    # FILTRO D'INGRESSO: la maggior parte delle righe non contiene recapiti.
    #
    # Un registro in modalita' diagnostica e' fatto per nove decimi di pacchetti
    # RTP, temporizzatori e contatori, dove un recapito non puo' esserci.
    # Applicare a tutte quelle righe quattro espressioni regolari con un blocco
    # di codice come valutatore costava minuti su un registro di centomila
    # righe, cioe' proprio sui registri che si mandano in analisi.
    if ($Riga.Length -lt 3) { return $Riga }
    if ($Riga -notmatch '[0-9]') { return $Riga }

    $risultato = $Riga

    # 1. Parte utente di un URI SIP: sip:NUMERO@dominio.
    #    Copre From, To, Contact, Refer-To, le richieste e i messaggi del
    #    programma. Il dominio resta: serve a riconoscere il centralino.
    if ($risultato.Contains('sip:') -or $risultato.Contains('sips:')) {
        $risultato = [regex]::Replace(
            $risultato,
            '(?<=sips?:)(\+?\d[\d\-\.]{1,19})(?=@)',
            { param($m) Get-EtichettaRecapito $m.Groups[1].Value })
    }

    # 2. Nome visualizzato fra virgolette, che nei registri porta tanto numeri
    #    quanto nominativi di persone.
    if ($risultato.Contains('"')) {
        $risultato = [regex]::Replace(
            $risultato,
            '"([^"]{1,120})"',
            { param($m) '"' + (Convert-NomeVisualizzato $m.Groups[1].Value) + '"' })
    }

    # 3. Frasi del programma che annunciano un recapito. Sono poche e note:
    #    elencarle costa meno che rischiare di toccare una porta o un CSeq.
    $frasi = @(
        '(?<=Chiamante )(\+?\d[\d\s]{2,19}\d)',
        '(?<=Incoming con )(\+?\d[\d\s]{2,19}\d)',
        '(?<=Outgoing con )(\+?\d[\d\s]{2,19}\d)',
        '(?<=Missed con )(\+?\d[\d\s]{2,19}\d)',
        '(?<=: da )(\+?\d[\d\s]{2,19}\d)',
        '(?<= verso )(\+?\d[\d\s]{2,19}\d)',
        '(?<=chiamata a )(\+?\d[\d\s]{2,19}\d)'
    )

    # Le frasi note costano una scansione ciascuna: si tentano solo se la riga
    # contiene una delle parole che le introducono.
    if ($risultato -match 'Chiamante|con |: da | verso |chiamata a ') {
        foreach ($frase in $frasi) {
            $risultato = [regex]::Replace(
                $risultato,
                $frase,
                { param($m) Get-EtichettaRecapito $m.Groups[1].Value })
        }
    }

    return $risultato
}

<#
.SYNOPSIS
    Prepara la passata finale che toglie i recapiti gia' riconosciuti anche dove
    compaiono fuori dalle posizioni note.

.DESCRIPTION
    Le regole precedenti riconoscono i recapiti in posizioni sicure. Ma la
    libreria SIP scrive anche frasi proprie - "Stopping SIP notifier user agent
    for user 7389" - e un elenco di frasi da inseguire sarebbe sempre indietro di
    una versione.

    Qui si sostituisce solo cio' che e' GIA' stato riconosciuto altrove come
    recapito: nessun numero nuovo viene interpretato, quindi la passata non puo'
    scambiare per un recapito una porta o un conteggio mai visto prima.

    UNA SOLA ESPRESSIONE, NON UNA PER NUMERO.

    Con duecento recapiti e centomila righe, ripetere una sostituzione per
    ciascuno significa venti milioni di passaggi: lo strumento diventava
    inutilizzabile proprio sui registri grandi, che sono quelli che si mandano in
    analisi. Un'alternanza compilata li cerca tutti in una lettura sola.

    I confini escludono le posizioni in cui una cifra non e' mai un recapito:
    dopo i due punti stanno le porte (:5061), dopo l'uguale i parametri
    (rport=52743), fra i punti gli indirizzi IP.
#>
function New-RicercaResidui {
    $perCifre = @{}

    foreach ($chiave in $recapiti.Keys) {
        $cifre = $chiave -replace '\D', ''

        # Sotto le tre cifre il rischio di colpire un contatore supera il
        # beneficio, e un recapito cosi' corto non identifica nessuno.
        if ($cifre.Length -lt 3) { continue }

        $perCifre[$cifre] = $recapiti[$chiave]
    }

    if ($perCifre.Count -eq 0) { return $null }

    # I piu' lunghi per primi: altrimenti un interno di quattro cifre contenuto
    # in un numero lungo verrebbe sostituito per primo, spezzandolo.
    $alternanza = ($perCifre.Keys |
        Sort-Object -Property Length -Descending |
        ForEach-Object { [regex]::Escape($_) }) -join '|'

    $espressione = [regex]::new(
        # I CONFINI ESCLUDONO LE LETTERE, NON SOLO LE CIFRE.
        #
        # Con il confine sulle sole cifre la sostituzione entrava dentro gli
        # identificativi esadecimali - "tag=d08a-c6d6d01d4712f59c" contiene una
        # sequenza preceduta da una lettera - e dentro i Call-ID, corrompendo
        # campi che alla diagnosi servono interi. Non e' una fuga di dati, e'
        # il registro rovinato: l'opposto di cio' che questo strumento promette.
        #
        # Escluso anche cio' che segue "CSeq: ", dove il numero e' un contatore
        # di transazione e non potra' mai essere un recapito.
        # Escluse anche le porte annunciate in chiaro dalla libreria - "on port
        # 50154" - perche' un interno di cinque cifre e una porta effimera hanno la
        # stessa forma, e li' la posizione non basta a distinguerli.
        '(?<!CSeq: )(?<!port )(?<!porta )(?<!Inviati )(?<!ricevuti )(?<!scartati )(?<!inserito )(?<!buffer )(?<!Pacchetti )(?<![\w:=\.])(' + $alternanza + ')(?![\w\.])',
        [System.Text.RegularExpressions.RegexOptions]::Compiled)

    return @{ Espressione = $espressione; Mappa = $perCifre }
}

function Remove-RecapitiResidui {
    param([string] $Riga, $Ricerca)

    if ($null -eq $Ricerca) { return $Riga }

    return $Ricerca.Espressione.Replace($Riga, {
        param($m)
        $Ricerca.Mappa[$m.Groups[1].Value]
    })
}

# ---------------------------------------------------------------------------
# Elaborazione
# ---------------------------------------------------------------------------

Write-Host "Anonimizzazione dei registri di BlindPhone" -ForegroundColor Cyan
Write-Host ""

$totaleRighe = 0

# Il registro da anonimizzare e' quasi sempre quello che BlindPhone sta
# scrivendo in questo momento: e' il file della giornata in corso, cioe' quello
# che contiene il guasto appena osservato. Aprirlo in lettura esclusiva
# fallirebbe sempre, e chiedere di chiudere il programma per leggere il registro
# significherebbe chiedere di interrompere il servizio.
function Read-RegistroInUso {
    param([string] $Percorso)

    $flusso = [System.IO.File]::Open(
        $Percorso,
        [System.IO.FileMode]::Open,
        [System.IO.FileAccess]::Read,
        [System.IO.FileShare]::ReadWrite -bor [System.IO.FileShare]::Delete)

    try {
        $lettore = New-Object System.IO.StreamReader($flusso, [System.Text.Encoding]::UTF8, $true)
        try { return $lettore.ReadToEnd() -split "`r?`n" }
        finally { $lettore.Dispose() }
    }
    finally {
        $flusso.Dispose()
    }
}

# ---------------------------------------------------------------------------
# Che cosa rifare e che cosa saltare
# ---------------------------------------------------------------------------
#
# I registri dei giorni passati non cambiano piu': rifarli a ogni giro costa
# minuti e produce esattamente gli stessi file. Quello di oggi invece cresce
# durante il turno, ed e' quello che serve.
#
# IL CRITERIO E' LA DATA, NON L'ESISTENZA DELLA COPIA. Saltare un registro solo
# perche' la copia esiste sarebbe peggio del difetto che si vuole togliere: la
# copia di oggi resterebbe ferma alla prima passata, e chi analizza si
# ritroverebbe a leggere un registro che si ferma prima del guasto senza
# accorgersene. E' successo il 12 agosto 2026 — la copia si fermava alle 08:41 e
# la chiamata da esaminare era delle 09:05.
#
# Si rifa' quando il registro e' piu' recente della sua copia. Con -Tutto si
# rifa' tutto, che serve quando cambiano le regole di anonimizzazione: le copie
# vecchie sono state prodotte con le regole vecchie.

$daFare = @()
$saltati = 0

foreach ($f in $file) {
    $uscita = Join-Path $Destinazione ($f.BaseName + '-anonimo' + $f.Extension)

    if (-not $Tutto -and (Test-Path $uscita)) {
        $copia = Get-Item $uscita

        if ($copia.LastWriteTimeUtc -ge $f.LastWriteTimeUtc) {
            $saltati++
            Write-Host ("  {0}: gia' anonimizzato, invariato dal {1:dd/MM HH:mm}" -f $f.Name, $f.LastWriteTime) -ForegroundColor DarkGray
            continue
        }
    }

    $daFare += $f
}

if ($saltati -gt 0) {
    Write-Host ""
    Write-Host ("Saltati {0} registri gia' anonimizzati. Usa -Tutto per rifarli comunque." -f $saltati)
    Write-Host ""
}

if ($daFare.Count -eq 0) {
    Write-Host "Niente da fare: tutte le copie sono aggiornate." -ForegroundColor Green
    Write-Host ""
    Write-Host ("File anonimi in {0}" -f $Destinazione)
    return
}

foreach ($f in $daFare) {
    $uscita = Join-Path $Destinazione ($f.BaseName + '-anonimo' + $f.Extension)

    $righe = Read-RegistroInUso $f.FullName

    # Due passate: la prima riconosce i recapiti nelle posizioni sicure di tutto
    # il file, la seconda li toglie ovunque compaiano. Separarle serve perche' un
    # recapito puo' comparire in una frase della libreria PRIMA di apparire in un
    # URI: con una passata sola la riga iniziale resterebbe in chiaro.
    $prima = foreach ($riga in $righe) { Convert-Riga $riga }

    $ricerca = New-RicercaResidui
    $convertite = foreach ($riga in $prima) { Remove-RecapitiResidui $riga $ricerca }

    [System.IO.File]::WriteAllLines($uscita, $convertite, [System.Text.UTF8Encoding]::new($false))

    $totaleRighe += $righe.Count
    Write-Host ("  {0} -> {1} ({2} righe)" -f $f.Name, (Split-Path $uscita -Leaf), $righe.Count)
}

Write-Host ""
Write-Host ("Recapiti sostituiti:   {0}" -f $recapiti.Count)
Write-Host ("Nominativi sostituiti: {0}" -f $nomi.Count)
Write-Host ("Righe elaborate:       {0}" -f $totaleRighe)

# ---------------------------------------------------------------------------
# Controllo finale
# ---------------------------------------------------------------------------
#
# Una sostituzione che credeva di aver finito e non ha finito e' peggio di
# nessuna sostituzione: chi manda il file lo fa credendolo pulito. Si rilegge
# quindi cio' che si e' scritto e si cercano i recapiti superstiti nelle stesse
# posizioni in cui si e' intervenuti.

$superstiti = 0

foreach ($f in Get-ChildItem -Path $Destinazione -Filter '*-anonimo.log' -File) {
    $trovati = Select-String -Path $f.FullName -Pattern 'sips?:\+?\d{3,}@' -AllMatches
    if ($trovati) { $superstiti += ($trovati | Measure-Object).Count }
}

Write-Host ""

if ($superstiti -gt 0) {
    Write-Host ("ATTENZIONE: {0} recapiti sono rimasti nei file anonimi. NON inviarli." -f $superstiti) -ForegroundColor Red
    exit 1
}

Write-Host "Controllo superato: nessun recapito riconoscibile nei file anonimi." -ForegroundColor Green

# ---------------------------------------------------------------------------
# Mappa
# ---------------------------------------------------------------------------

if (-not [string]::IsNullOrWhiteSpace($Mappa)) {
    $contenuto = New-Object System.Collections.Generic.List[string]

    $contenuto.Add('# CORRISPONDENZA FRA ETICHETTE E RECAPITI VERI')
    $contenuto.Add('#')
    $contenuto.Add('# QUESTO FILE NON VA INVIATO A NESSUNO.')
    $contenuto.Add('#')
    $contenuto.Add('# Serve a te per risalire a chi era "ESTERNO-03" quando la diagnosi lo')
    $contenuto.Add('# nomina. Insieme al registro anonimo ricostruisce esattamente i dati che')
    $contenuto.Add('# l''anonimizzazione doveva proteggere: tenuti separati non dicono nulla,')
    $contenuto.Add('# messi insieme dicono tutto.')
    $contenuto.Add('#')
    $contenuto.Add(('# Prodotto il {0}' -f (Get-Date -Format 'dd/MM/yyyy HH:mm')))
    $contenuto.Add('')

    foreach ($chiave in $recapiti.Keys) { $contenuto.Add(('{0} = {1}' -f $recapiti[$chiave], $chiave)) }
    foreach ($chiave in $nomi.Keys) { $contenuto.Add(('{0} = {1}' -f $nomi[$chiave], $chiave)) }

    [System.IO.File]::WriteAllLines($Mappa, $contenuto, [System.Text.UTF8Encoding]::new($false))

    Write-Host ""
    Write-Host ("Mappa scritta in {0}" -f $Mappa) -ForegroundColor Yellow
    Write-Host "  Non allegarla ai file anonimi: insieme ricostruiscono i dati originali." -ForegroundColor Yellow
}

Write-Host ""
Write-Host ("File anonimi in {0}" -f $Destinazione)
