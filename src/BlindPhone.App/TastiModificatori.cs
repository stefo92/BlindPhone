using System.Windows.Input;
using BlindPhone.Core.Domain;

namespace BlindPhone.App;

/// <summary>
/// Traduce i modificatori di tastiera di WPF in quelli delle scorciatoie.
/// </summary>
/// <remarks>
/// Sta qui e non in <c>BlindPhone.Core</c> perché <see cref="ModifierKeys"/> è un tipo
/// di WPF, e il dominio non deve dipendere dall'interfaccia. Lo usano la finestra che
/// cattura una nuova combinazione e la finestra principale che la riconosce: devono
/// leggere i modificatori nello stesso modo, altrimenti una combinazione registrata
/// non verrebbe più riconosciuta.
/// </remarks>
internal static class TastiModificatori
{
    /// <summary>Modificatori premuti, nella forma delle scorciatoie.</summary>
    /// <param name="keys">Modificatori come li riporta WPF.</param>
    public static HotkeyModifiers DaWpf(ModifierKeys keys)
    {
        var result = HotkeyModifiers.None;
        if (keys.HasFlag(ModifierKeys.Control)) result |= HotkeyModifiers.Control;
        if (keys.HasFlag(ModifierKeys.Alt)) result |= HotkeyModifiers.Alt;
        if (keys.HasFlag(ModifierKeys.Shift)) result |= HotkeyModifiers.Shift;
        if (keys.HasFlag(ModifierKeys.Windows)) result |= HotkeyModifiers.Windows;
        return result;
    }
}
