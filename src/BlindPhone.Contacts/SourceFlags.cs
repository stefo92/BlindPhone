namespace BlindPhone.Contacts;

/// <summary>
/// Lettura dei contrassegni sì/no di una rubrica, qualunque sia la provenienza.
/// </summary>
/// <remarks>
/// Sta in un punto solo perché tabelle e file CSV devono riconoscere le stesse forme:
/// un contrassegno che vale «sì» in un backup e «no» nello stesso dato esportato in CSV
/// cambierebbe la rubrica a seconda di come l'ente ha consegnato il file.
/// </remarks>
internal static class SourceFlags
{
    /// <summary>
    /// Interpreta come vero un contrassegno letto da una tabella o da un file.
    /// </summary>
    /// <param name="valore">Valore della colonna, come stringa.</param>
    /// <remarks>
    /// Lo stesso contrassegno arriva come <c>1</c> da SQL Server, come <c>True</c>
    /// dal lettore che converte i bit in testo e come <c>Sì</c> da un CSV compilato a
    /// mano. Riconoscere una sola di queste forme significherebbe importare come
    /// contatti gli ottocento numeri brevi di una rubrica, o tenere in elenco le voci
    /// disattivate: due errori che si notano contando le voci, non guardandole.
    /// </remarks>
    public static bool IsTrue(string? valore)
    {
        if (string.IsNullOrWhiteSpace(valore)) return false;

        var v = valore.Trim();

        return v is "1"
            || v.Equals("true", StringComparison.OrdinalIgnoreCase)
            || v.Equals("si", StringComparison.OrdinalIgnoreCase)
            || v.Equals("sì", StringComparison.OrdinalIgnoreCase)
            || v.Equals("yes", StringComparison.OrdinalIgnoreCase)
            || v.Equals("y", StringComparison.OrdinalIgnoreCase);
    }
}
