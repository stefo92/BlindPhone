using System.Globalization;
using System.Text;
using BlindPhone.Core.Domain;

namespace BlindPhone.Core.Services;

/// <summary>
/// Genera la scheda dei tasti rapidi del profilo attivo.
/// </summary>
/// <remarks>
/// <para>
/// La scheda serve a due usi che l'elenco letto a voce non copre. Il primo è la
/// stampa braille: il file di testo prodotto qui si manda a un display o a una
/// stampante braille e diventa il foglio che sta accanto alla tastiera nelle prime
/// settimane. Il secondo è il collega vedente che affianca l'operatore in
/// formazione e ha bisogno di leggere la stessa mappa.
/// </para>
/// <para>
/// <b>Perché è testo semplice e non un formato ricco.</b> Le righe corte, senza
/// tabelle né rientri decorativi, sono l'unica forma che si legge bene sia su un
/// display braille da 40 celle sia con la lettura continua di uno screen reader. Una
/// tabella impaginata a colonne, che a schermo sarebbe più ordinata, in braille
/// diventa illeggibile.
/// </para>
/// <para>
/// Compaiono anche i comandi senza combinazione assegnata. Sapere che una funzione
/// esiste ed è libera è esattamente l'informazione che porta a configurarla; se le
/// voci non assegnate sparissero, l'operatore non saprebbe mai di poterle avere.
/// </para>
/// </remarks>
public static class HotkeyCardWriter
{
    /// <summary>
    /// Compone la scheda dei tasti di un profilo.
    /// </summary>
    /// <param name="profile">Profilo da descrivere.</param>
    /// <param name="generatedAt">Momento della generazione, riportato in intestazione.</param>
    public static string Build(HotkeyProfile profile, DateTimeOffset generatedAt)
    {
        var text = new StringBuilder();

        text.AppendLine($"BlindPhone - Scheda tasti rapidi - Profilo {profile.Name}");
        text.AppendLine(CultureInfo.GetCultureInfo("it-IT"),
            $"Generata il {generatedAt:dd/MM/yyyy} alle {generatedAt:HH:mm}");
        text.AppendLine();
        text.AppendLine("Legenda: Locale funziona con BlindPhone in primo piano.");
        text.AppendLine("Globale funziona anche con BlindPhone in secondo piano.");
        text.AppendLine();

        foreach (var category in HotkeyCommandCatalog.Categories)
        {
            text.AppendLine(HotkeyCommandCatalog.CategoryLabel(category));
            text.AppendLine();

            foreach (var info in HotkeyCommandCatalog.InCategory(category))
            {
                text.AppendLine(DescribeCommand(profile, info));
            }

            text.AppendLine();
        }

        var unassigned = HotkeyCommandCatalog.All.Count(info =>
            profile.Get(info.Command, HotkeyScope.Local) is null
            && profile.Get(info.Command, HotkeyScope.Global) is null);

        if (unassigned > 0)
        {
            text.AppendLine($"Comandi senza alcuna combinazione: {unassigned}.");
            text.AppendLine("Si assegnano da Impostazioni, scheda Tasti rapidi.");
            text.AppendLine();
        }

        return text.ToString();
    }

    /// <summary>
    /// Compone la riga di un comando, in forma leggibile a voce.
    /// </summary>
    /// <remarks>
    /// Si usa <see cref="HotkeyBinding.SpeechText"/> e non la forma compatta con i
    /// segni più: "Control Maiusc F9" si sente correttamente al primo ascolto e
    /// occupa meno celle braille di "Ctrl+Maiusc+F9", dove ogni segno di punteggiatura
    /// è un carattere da decifrare.
    /// </remarks>
    private static string DescribeCommand(HotkeyProfile profile, HotkeyCommandInfo info)
    {
        var local = profile.Get(info.Command, HotkeyScope.Local);
        var global = profile.Get(info.Command, HotkeyScope.Global);

        if (local is null && global is null) return $"- {info.Label}: non assegnato";

        var parts = new List<string>();
        if (local is not null) parts.Add($"Locale {local.SpeechText}");
        if (global is not null) parts.Add($"Globale {global.SpeechText}");

        return $"- {info.Label}: {string.Join(" - ", parts)}";
    }
}
