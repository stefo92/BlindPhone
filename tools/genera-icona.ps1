<#
.SYNOPSIS
    Genera l'icona dell'applicazione e la grafica della procedura di
    installazione, a partire dall'unica immagine sorgente.

.DESCRIPTION
    Windows non ridimensiona una sola immagine: sceglie fra le dimensioni
    contenute nel file .ico quella che gli serve, e se non la trova ne adatta
    un'altra con un risultato scadente. Le dimensioni prodotte coprono i punti in
    cui l'icona compare davvero: 16 e 24 nella barra del titolo e nell'elenco
    programmi, 32 e 48 sul desktop e in Esplora risorse, 64 e 128 nelle viste a
    icone grandi, 256 nella finestra di disinstallazione e nelle anteprime.

    Le dimensioni fino a 128 sono in formato BMP a 32 bit, la sola 256 in PNG:
    e' la convenzione che ogni versione di Windows legge senza incertezze.

    Vengono prodotte anche le due immagini della procedura di installazione, nelle
    misure fisse che l'installatore di Windows si aspetta: la striscia in alto
    (493 per 58) e il riquadro laterale della prima e dell'ultima pagina (493 per
    312). Sono a 24 bit e senza trasparenza, perche' le finestre di dialogo
    dell'installatore non compongono il canale alfa e lo renderebbero nero.

.PARAMETER Sorgente
    Immagine di partenza, quadrata e con sfondo trasparente o pieno.

.PARAMETER Destinazione
    File .ico da scrivere.

.PARAMETER CartellaInstallatore
    Cartella in cui scrivere la grafica della procedura di installazione.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools\genera-icona.ps1

.NOTES
    Va rieseguito solo se cambia l'immagine sorgente. I file prodotti fanno parte
    del codice sorgente: il .ico e' referenziato da BlindPhone.App.csproj, le due
    immagini da installer\BlindPhone.wxs.
#>

[CmdletBinding()]
param(
    [string] $Sorgente,
    [string] $Destinazione,
    [string] $CartellaInstallatore
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

# La radice del progetto si ricava dalla posizione dello script, non dalla
# cartella corrente: cosi' lo script funziona anche lanciato da altrove. Con
# alcuni host $PSScriptRoot resta vuoto, e allora si ripiega sul percorso del
# comando.
$radice = $PSScriptRoot
if ([string]::IsNullOrEmpty($radice)) { $radice = Split-Path -Parent $PSCommandPath }
if ([string]::IsNullOrEmpty($radice)) { $radice = (Get-Location).Path }
$radice = Split-Path -Parent $radice

if ([string]::IsNullOrEmpty($Sorgente)) { $Sorgente = Join-Path $radice 'icona.png' }
if ([string]::IsNullOrEmpty($Destinazione)) {
    $Destinazione = Join-Path $radice 'src\BlindPhone.App\blindphone.ico'
}
if ([string]::IsNullOrEmpty($CartellaInstallatore)) {
    $CartellaInstallatore = Join-Path $radice 'installer'
}

$Sorgente = (Resolve-Path $Sorgente).Path
$Destinazione = [System.IO.Path]::GetFullPath($Destinazione)

# Oltre i 256 pixel il formato .ico non va, e sotto i 16 nessuna interfaccia di
# Windows chiede l'icona.
$dimensioni = @(16, 24, 32, 48, 64, 128, 256)

function New-Ridimensionata {
    param([System.Drawing.Image] $Origine, [int] $Lato)

    $bitmap = New-Object System.Drawing.Bitmap($Lato, $Lato, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $grafica = [System.Drawing.Graphics]::FromImage($bitmap)

    try {
        # La qualita' alta conta soprattutto sulle dimensioni piccole: e' li' che
        # un ridimensionamento grezzo trasforma il portatile in una macchia.
        $grafica.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $grafica.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $grafica.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
        $grafica.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
        $grafica.Clear([System.Drawing.Color]::Transparent)
        $grafica.DrawImage($Origine, (New-Object System.Drawing.Rectangle(0, 0, $Lato, $Lato)))
    }
    finally {
        $grafica.Dispose()
    }

    return $bitmap
}

function Get-BytesBmp {
    param([System.Drawing.Bitmap] $Bitmap)

    $larghezza = $Bitmap.Width
    $altezza = $Bitmap.Height

    $rettangolo = New-Object System.Drawing.Rectangle(0, 0, $larghezza, $altezza)
    $dati = $Bitmap.LockBits(
        $rettangolo,
        [System.Drawing.Imaging.ImageLockMode]::ReadOnly,
        [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)

    $buffer = New-Object byte[] ($dati.Stride * $altezza)
    [System.Runtime.InteropServices.Marshal]::Copy($dati.Scan0, $buffer, 0, $buffer.Length)
    $passo = $dati.Stride
    $Bitmap.UnlockBits($dati)

    $flusso = New-Object System.IO.MemoryStream
    $scrittore = New-Object System.IO.BinaryWriter($flusso)

    try {
        # BITMAPINFOHEADER. L'altezza e' doppia perche' la struttura descrive due
        # immagini sovrapposte: i colori e la maschera di trasparenza.
        $scrittore.Write([uint32] 40)
        $scrittore.Write([int32] $larghezza)
        $scrittore.Write([int32] ($altezza * 2))
        $scrittore.Write([uint16] 1)
        $scrittore.Write([uint16] 32)
        $scrittore.Write([uint32] 0)
        $scrittore.Write([uint32] ($larghezza * $altezza * 4))
        $scrittore.Write([int32] 0)
        $scrittore.Write([int32] 0)
        $scrittore.Write([uint32] 0)
        $scrittore.Write([uint32] 0)

        # I pixel, dal basso verso l'alto: e' l'ordine del formato BMP.
        for ($y = $altezza - 1; $y -ge 0; $y--) {
            $scrittore.Write($buffer, $y * $passo, $larghezza * 4)
        }

        # Maschera di trasparenza a 1 bit, righe allineate a 4 byte. Resta a zero:
        # la trasparenza vera sta nel canale alfa dei pixel, e questa maschera
        # serve solo alle versioni di Windows che non lo leggono.
        $byteRiga = [math]::Floor(($larghezza + 31) / 32) * 4
        $scrittore.Write((New-Object byte[] ($byteRiga * $altezza)))

        $scrittore.Flush()

        # La virgola non e' un refuso: senza, PowerShell srotola l'array nel flusso
        # di uscita e chi riceve si ritrova un Object[] di byte al posto di un
        # byte[]. Il conteggio degli elementi resta giusto, ma BinaryWriter non
        # riconosce piu' il tipo e scrive un byte solo.
        return , $flusso.ToArray()
    }
    finally {
        $scrittore.Dispose()
        $flusso.Dispose()
    }
}

function Get-BytesPng {
    param([System.Drawing.Bitmap] $Bitmap)

    $flusso = New-Object System.IO.MemoryStream

    try {
        $Bitmap.Save($flusso, [System.Drawing.Imaging.ImageFormat]::Png)
        return , $flusso.ToArray()
    }
    finally {
        $flusso.Dispose()
    }
}

Write-Host "Sorgente:     $Sorgente"

$origine = [System.Drawing.Image]::FromFile($Sorgente)

try {
    if ($origine.Width -ne $origine.Height) {
        Write-Warning "L'immagine sorgente non e' quadrata ($($origine.Width)x$($origine.Height)): verra' deformata."
    }

    $immagini = @()

    foreach ($lato in $dimensioni) {
        $bitmap = New-Ridimensionata -Origine $origine -Lato $lato

        try {
            $bytes = if ($lato -ge 256) { Get-BytesPng $bitmap } else { Get-BytesBmp $bitmap }
            $immagini += [pscustomobject]@{ Lato = $lato; Bytes = $bytes }
            Write-Host ("  {0,3}x{1,-3} {2,8:N0} byte" -f $lato, $lato, $bytes.Length)
        }
        finally {
            $bitmap.Dispose()
        }
    }
}
finally {
    $origine.Dispose()
}

$cartella = Split-Path -Parent $Destinazione
if (-not (Test-Path $cartella)) { New-Item -ItemType Directory -Path $cartella -Force | Out-Null }

$uscita = [System.IO.File]::Create($Destinazione)
$scrittore = New-Object System.IO.BinaryWriter($uscita)

try {
    # ICONDIR
    $scrittore.Write([uint16] 0)
    $scrittore.Write([uint16] 1)
    $scrittore.Write([uint16] $immagini.Count)

    # Le immagini cominciano dopo la testata e l'elenco delle voci.
    $posizione = 6 + (16 * $immagini.Count)

    foreach ($immagine in $immagini) {
        # 256 si scrive come 0: il campo e' di un byte solo.
        $lato = if ($immagine.Lato -ge 256) { 0 } else { $immagine.Lato }

        $scrittore.Write([byte] $lato)
        $scrittore.Write([byte] $lato)
        $scrittore.Write([byte] 0)
        $scrittore.Write([byte] 0)
        $scrittore.Write([uint16] 1)
        $scrittore.Write([uint16] 32)
        $scrittore.Write([uint32] $immagine.Bytes.Length)
        $scrittore.Write([uint32] $posizione)

        $posizione += $immagine.Bytes.Length
    }

    foreach ($immagine in $immagini) {
        $scrittore.Write([byte[]] $immagine.Bytes)
    }
}
finally {
    $scrittore.Dispose()
    $uscita.Dispose()
}

$dimensione = (Get-Item $Destinazione).Length
Write-Host "Destinazione: $Destinazione ($('{0:N0}' -f $dimensione) byte, $($immagini.Count) risoluzioni)"

# ---------------------------------------------------------------------------
# Grafica della procedura di installazione
# ---------------------------------------------------------------------------

function New-ImmagineInstallatore {
    param(
        [System.Drawing.Image] $Origine,
        [int] $Larghezza,
        [int] $Altezza,
        [int] $LatoIcona,
        [int] $X,
        [int] $Y,
        [string] $File
    )

    # 24 bit e sfondo pieno: le finestre di dialogo dell'installatore di Windows
    # non compongono il canale alfa, e una immagine con trasparenza vi comparirebbe
    # con lo sfondo nero.
    $tela = New-Object System.Drawing.Bitmap($Larghezza, $Altezza, [System.Drawing.Imaging.PixelFormat]::Format24bppRgb)
    $grafica = [System.Drawing.Graphics]::FromImage($tela)

    try {
        $grafica.Clear([System.Drawing.Color]::White)
        $grafica.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $grafica.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $grafica.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
        $grafica.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
        $grafica.DrawImage($Origine, (New-Object System.Drawing.Rectangle($X, $Y, $LatoIcona, $LatoIcona)))
    }
    finally {
        $grafica.Dispose()
    }

    $tela.Save($File, [System.Drawing.Imaging.ImageFormat]::Bmp)
    $tela.Dispose()

    Write-Host ("  {0,-12} {1}x{2}" -f (Split-Path -Leaf $File), $Larghezza, $Altezza)
}

if (-not (Test-Path $CartellaInstallatore)) {
    New-Item -ItemType Directory -Path $CartellaInstallatore -Force | Out-Null
}

$origine = [System.Drawing.Image]::FromFile($Sorgente)

try {
    Write-Host "Grafica della procedura di installazione:"

    # Striscia superiore: il testo della pagina sta a sinistra, l'icona va a destra.
    New-ImmagineInstallatore -Origine $origine -Larghezza 493 -Altezza 58 `
        -LatoIcona 44 -X 435 -Y 7 `
        -File (Join-Path $CartellaInstallatore 'striscia.bmp')

    # Riquadro della prima e dell'ultima pagina: e' visibile la sola fascia
    # sinistra, larga circa 164 punti, perche' il resto sta sotto al testo.
    New-ImmagineInstallatore -Origine $origine -Larghezza 493 -Altezza 312 `
        -LatoIcona 110 -X 27 -Y 60 `
        -File (Join-Path $CartellaInstallatore 'riquadro.bmp')
}
finally {
    $origine.Dispose()
}
