using System.Globalization;
using System.Text.RegularExpressions;
using BlindPhone.Core.Domain;

namespace BlindPhone.Core.Services;

/// <summary>
/// Legge il corpo delle notifiche della segreteria telefonica (RFC 3842).
/// </summary>
/// <remarks>
/// <para>
/// Il corpo è un testo di poche righe nella forma delle intestazioni SIP, con tipo
/// <c>application/simple-message-summary</c>:
/// </para>
/// <code>
/// Messages-Waiting: yes
/// Message-Account: sip:*97@centralino.ente.it
/// Voice-Message: 2/8 (1/0)
/// </code>
/// <para>
/// Cioè: ci sono messaggi da ascoltare; la casella si raggiunge a quell'indirizzo; due
/// messaggi vocali nuovi e otto già ascoltati, di cui uno nuovo urgente.
/// </para>
/// <para>
/// <b>La lettura è tollerante per scelta.</b> I centralini in esercizio scrivono questo
/// testo ciascuno a modo suo: maiuscole diverse, spazi attorno alla barra, righe di fax
/// e di testo in mezzo, la parte fra parentesi assente, a volte nessun conteggio. Una
/// riga che non si capisce si salta e non fa perdere le altre; un numero assurdo — un
/// contatore impazzito, un campo negativo — diventa «non si sa quanti» e non un
/// annuncio di quattro miliardi di messaggi.
/// </para>
/// <para>
/// Sta nel core e non accanto al codice SIP perché è la parte che decide cosa
/// l'operatore sentirà, ed è l'unica che si possa verificare con i corpi dei centralini
/// veri senza avere un centralino.
/// </para>
/// </remarks>
public static partial class MessageSummaryParser
{
    /// <summary>
    /// Conteggio più alto che si prende per vero.
    /// </summary>
    /// <remarks>
    /// Le caselle dei centralini più diffusi si fermano a qualche centinaio di messaggi,
    /// e le più capienti a qualche migliaio. Oltre questa soglia il numero non descrive
    /// una casella ma un difetto, e pronunciarlo farebbe solo dubitare dell'annuncio.
    /// </remarks>
    public const int MaxPlausibleCount = 9999;

    /// <summary>
    /// Interpreta il corpo di una notifica.
    /// </summary>
    /// <param name="body">Corpo del <c>NOTIFY</c>, così come è arrivato.</param>
    /// <returns>
    /// Lo stato della segreteria, oppure <c>null</c> se il corpo non dice nulla di
    /// utilizzabile: vuoto, oppure senza né la riga della spia né i conteggi.
    /// </returns>
    /// <remarks>
    /// <para>
    /// <b>Se manca la riga della spia si ricava dai conteggi.</b> La norma la vuole
    /// sempre, ma un corpo che dice «due messaggi nuovi» senza dire «ci sono messaggi»
    /// non lascia dubbi su cosa intendesse; scartarlo significherebbe tacere una notizia
    /// che c'era.
    /// </para>
    /// <para>
    /// Di ogni riga vale la prima occorrenza: un corpo che ne ripete una è difettoso, e
    /// la prima è quella che i telefoni da tavolo leggono.
    /// </para>
    /// </remarks>
    public static MessageSummary? Parse(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;

        string? spia = null;
        string? casella = null;
        string? vocali = null;

        foreach (var grezza in body.Split('\n'))
        {
            var riga = grezza.Trim().TrimStart('﻿');

            var separatore = riga.IndexOf(':');
            if (separatore <= 0) continue;

            var nome = riga[..separatore].Trim();
            var valore = riga[(separatore + 1)..].Trim();

            if (nome.Equals("Messages-Waiting", StringComparison.OrdinalIgnoreCase)) spia ??= valore;
            else if (nome.Equals("Message-Account", StringComparison.OrdinalIgnoreCase)) casella ??= valore;
            else if (nome.Equals("Voice-Message", StringComparison.OrdinalIgnoreCase)) vocali ??= valore;
        }

        var (nuovi, vecchi, urgentiNuovi, urgentiVecchi) = ParseCounts(vocali);

        bool attesa;

        switch (FirstToken(spia))
        {
            case "yes":
                attesa = true;
                break;

            case "no":
                attesa = false;
                break;

            default:
                if (nuovi is null) return null;
                attesa = nuovi > 0;
                break;
        }

        return new MessageSummary(attesa, CleanAccount(casella), nuovi, vecchi, urgentiNuovi, urgentiVecchi);
    }

    /// <summary>La prima parola di un valore, in minuscolo: «Yes ; commento» diventa «yes».</summary>
    private static string? FirstToken(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;

        var fine = value.IndexOfAny([' ', '\t', ';', ',']);
        var parola = fine < 0 ? value : value[..fine];

        return parola.ToLowerInvariant();
    }

    /// <summary>Legge <c>nuovi/vecchi (urgenti nuovi/urgenti vecchi)</c>, con la parte fra parentesi facoltativa.</summary>
    private static (int? New, int? Old, int? UrgentNew, int? UrgentOld) ParseCounts(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return (null, null, null, null);

        var corrispondenza = CountsPattern().Match(value);
        if (!corrispondenza.Success) return (null, null, null, null);

        return (
            Count(corrispondenza.Groups["nuovi"]),
            Count(corrispondenza.Groups["vecchi"]),
            Count(corrispondenza.Groups["urgentiNuovi"]),
            Count(corrispondenza.Groups["urgentiVecchi"]));
    }

    private static int? Count(Group group)
    {
        if (!group.Success) return null;

        // Un numero che non sta in un intero, o che supera ogni casella reale, e' un
        // difetto del centralino e non un conteggio: si dice «non si sa quanti».
        return int.TryParse(group.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var numero)
               && numero <= MaxPlausibleCount
            ? numero
            : null;
    }

    /// <summary>Toglie le parentesi angolari che alcuni centralini mettono attorno all'indirizzo.</summary>
    private static string? CleanAccount(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;

        var pulito = value.Trim().TrimStart('<').TrimEnd('>').Trim();
        return pulito.Length == 0 ? null : pulito;
    }

    // Cifre [0-9] e non \d: \d accetta anche le cifre di altri alfabeti, che nessun
    // centralino scrive e che int.TryParse rifiuterebbe comunque.
    [GeneratedRegex(
        @"^(?<nuovi>[0-9]+)\s*/\s*(?<vecchi>[0-9]+)(?:\s*\(\s*(?<urgentiNuovi>[0-9]+)\s*/\s*(?<urgentiVecchi>[0-9]+)\s*\))?",
        RegexOptions.CultureInvariant)]
    private static partial Regex CountsPattern();
}
