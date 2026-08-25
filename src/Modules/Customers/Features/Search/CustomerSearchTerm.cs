using GridCore.Modules.Customers.Features.Shared;

namespace GridCore.Modules.Customers.Features.Search;

/// <summary>
/// What a rep typed, classified — the normalised forms of it and the kinds of match worth
/// dispatching for it.
/// </summary>
/// <param name="Raw">Exactly what was typed, kept so a "no matches for …" message can quote it.</param>
/// <param name="Normalised">Lower-cased and whitespace-collapsed; what account, meter and name comparisons use.</param>
/// <param name="Digits">The digits alone; what a telephone comparison uses.</param>
/// <param name="NormalisedAddress">The address-equivalence form; what an address comparison uses.</param>
/// <param name="AddressToken">The token an address candidate query narrows on.</param>
/// <param name="Kinds">The kinds to look in, in <see cref="CustomerMatchKind"/> precedence order.</param>
public sealed record CustomerSearchTerm(
    string Raw,
    string Normalised,
    string Digits,
    string NormalisedAddress,
    string AddressToken,
    IReadOnlyList<CustomerMatchKind> Kinds)
{
    /// <summary>
    /// Shortest run of digits that is a telephone number rather than a fragment of an identifier.
    /// </summary>
    /// <remarks>
    /// Seven is the length of a local subscriber number, which is the shortest thing anybody dials.
    /// Below it a bare number is far more likely to be the tail of an account or a meter — a rep who
    /// has "12" in front of them means <c>C-000012</c>, not somebody's telephone.
    /// </remarks>
    public const int ShortestPhone = 7;

    /// <summary>Whether there is nothing to look for. Not an error: an empty box is the resting state.</summary>
    public bool IsEmpty => Kinds.Count is 0;

    /// <summary>
    /// Classifies <paramref name="term"/> into the kinds worth dispatching.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The rules, in order:
    /// </para>
    /// <list type="bullet">
    /// <item>Nothing, or only punctuation, dispatches nothing — there is no letter and no digit in
    /// it, so there is nothing any of the five could be asked about.</item>
    /// <item>
    /// A <b>letter-prefixed identifier</b> — one to four letters, an optional dash, then digits —
    /// is an account or a meter number and nothing else. That covers <c>C-000012</c>,
    /// <c>MTR-000007</c> and the <c>c12</c> a rep types when they are in a hurry. GridCore issues no
    /// identifier without a letter prefix (<see cref="CustomerNumbers"/>), which is what makes this
    /// rule decidable rather than a guess.
    /// </item>
    /// <item>
    /// <b>Digits and punctuation only</b>, with <see cref="ShortestPhone"/> digits or more, is a
    /// telephone number. Fewer than that, and it is dispatched as an account, a meter <i>and</i> a
    /// phone: a short run of digits is genuinely ambiguous, and precedence sorts out which one the
    /// rep meant when more than one answers.
    /// </item>
    /// <item>Anything else is a name or an address, both.</item>
    /// </list>
    /// <para>
    /// Note that ambiguity is answered by dispatching <b>more</b> kinds rather than by choosing one.
    /// Choosing would mean a rep who typed a real account number occasionally getting nothing back
    /// because the classifier decided it was a telephone; dispatching both costs one extra bounded
    /// query and cannot be wrong.
    /// </para>
    /// </remarks>
    public static CustomerSearchTerm Classify(string? term)
    {
        var raw = term?.Trim() ?? string.Empty;
        var normalised = SearchText.Normalise(raw);
        var digits = SearchText.Digits(raw);
        var normalisedAddress = SearchText.NormaliseAddress(raw);
        var addressToken = SearchText.MostSelectiveToken(raw);

        return new CustomerSearchTerm(
            raw,
            normalised,
            digits,
            normalisedAddress,
            addressToken,
            KindsFor(normalised, digits));
    }

    private static IReadOnlyList<CustomerMatchKind> KindsFor(string normalised, string digits)
    {
        if (!normalised.Any(char.IsLetterOrDigit))
        {
            return [];
        }

        if (IsPrefixedIdentifier(normalised))
        {
            return [CustomerMatchKind.AccountNumber, CustomerMatchKind.MeterNumber];
        }

        if (digits.Length > 0 && normalised.All(character => !char.IsAsciiLetter(character)))
        {
            return digits.Length >= ShortestPhone
                ? [CustomerMatchKind.Phone]
                : [CustomerMatchKind.AccountNumber, CustomerMatchKind.MeterNumber, CustomerMatchKind.Phone];
        }

        return [CustomerMatchKind.Name, CustomerMatchKind.Address];
    }

    /// <summary>
    /// Whether <paramref name="normalised"/> has the shape of a registry number: one to four
    /// letters, an optional dash, then at least one digit, and nothing else.
    /// </summary>
    private static bool IsPrefixedIdentifier(string normalised)
    {
        var index = 0;

        while (index < normalised.Length && char.IsAsciiLetterLower(normalised[index]))
        {
            index++;
        }

        if (index is 0 or > 4)
        {
            return false;
        }

        if (index < normalised.Length && normalised[index] is '-')
        {
            index++;
        }

        // At least one digit, and every remaining character a digit: "c-000012" qualifies, "cruz"
        // has no digits and "12 beach st" starts with one, so neither reaches here.
        return index < normalised.Length && normalised[index..].All(char.IsAsciiDigit);
    }
}
