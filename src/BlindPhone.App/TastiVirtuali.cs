using System.Windows.Input;
using BlindPhone.Core.Domain;

namespace BlindPhone.App;

/// <summary>
/// Traduce una combinazione nel codice di tasto virtuale che Windows vuole per
/// registrarla come scorciatoia globale.
/// </summary>
/// <remarks>
/// <para>
/// Sta qui per la stessa ragione di <see cref="TastiModificatori"/>: la conversione
/// passa da tipi di WPF, e il dominio non deve dipenderne. E deve stare in un punto
/// solo, perché la usano due finestre che devono arrivare allo stesso codice: la
/// principale, che registra le scorciatoie, e le impostazioni, che provano se una
/// combinazione nuova si potrà registrare. Se le due conversioni divergessero, la
/// prova direbbe «libera» per un tasto che poi la finestra principale registra in un
/// altro modo.
/// </para>
/// <para>
/// Il nome del tasto passa da <see cref="HotkeyKeyNames.RegistrationKey"/>: con
/// Control premuto Pausa diventa Interr, e il codice da dichiarare è quello.
/// </para>
/// </remarks>
internal static class TastiVirtuali
{
    /// <summary>Codice virtuale del tasto della combinazione, oppure 0 se il nome non è un tasto.</summary>
    /// <param name="binding">Combinazione da registrare.</param>
    public static uint Codice(HotkeyBinding binding)
        => Enum.TryParse<Key>(HotkeyKeyNames.RegistrationKey(binding.Key, binding.Modifiers), ignoreCase: true, out var key)
            ? (uint)KeyInterop.VirtualKeyFromKey(key)
            : 0;
}
