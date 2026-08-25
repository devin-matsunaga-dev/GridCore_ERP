using System.Text;

namespace GridCore.Modules.Customers.Features.Search;

/// <summary>
/// The normalisations the CSR search box compares through: case and whitespace, telephone
/// punctuation, and the street-type abbreviations that make "12 Beach St" and "12 Beach Street" the
/// same address.
/// </summary>
/// <remarks>
/// <para>
/// Pure, static and allocation-cheap — no database, no clock, no services. That is deliberate: this
/// is where every interesting rule in WP-2.9 lives, so it is where the fast tests are, and the
/// search service reduces to "narrow in SQL, then ask these functions".
/// </para>
/// <para>
/// Every comparison runs both sides through the same function. That is the whole design: the stored
/// value is normalised on the way out of the database and the typed value on the way in, so no rule
/// here has to be right about English — only consistent.
/// </para>
/// </remarks>
public static class SearchText
{
    /// <summary>
    /// Characters stripped out of a telephone number before it is compared.
    /// </summary>
    /// <remarks>
    /// <b>Mirrored in SQL</b> by <c>CustomerSearchService</c>'s <c>Replace</c> chain, which is how
    /// the candidate query strips the same punctuation off the stored column. The two are kept in
    /// step by <c>CustomerSearchServiceTests</c>, which runs the real query against punctuated
    /// numbers rather than trusting the two lists to agree.
    /// </remarks>
    public static readonly IReadOnlyList<string> PhonePunctuation = ["(", ")", "-", " ", ".", "+", "/"];

    /// <summary>
    /// Address tokens that mean the same thing, mapped to one canonical token each.
    /// </summary>
    /// <remarks>
    /// <para>
    /// An <b>equivalence table, not a dictionary of English</b>. The canonical token on the right is
    /// arbitrary — it exists to make two spellings compare equal, and it is never shown to anybody.
    /// A row always renders the address as it is stored.
    /// </para>
    /// <para>
    /// So <c>st</c> and <c>saint</c> deliberately collapse into the same class, and so do <c>dr</c>
    /// and <c>drive</c>. "St Joseph Street" and "Saint Joseph St" therefore match each other, which
    /// is the point; "Saint Joseph Street" and "St Joseph St" also match, which is a slight
    /// over-match and is harmless — the rep is looking at the real address on the row. The
    /// alternative is deciding whether a given "St" means Saint or Street from its position, which
    /// is guesswork that fails differently on the typed side (a street line) and the stored side
    /// (a whole one-line address with a town and an island after it).
    /// </para>
    /// </remarks>
    public static readonly IReadOnlyDictionary<string, string> AddressEquivalents =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["st"] = "street", ["str"] = "street", ["street"] = "street", ["saint"] = "street",
            ["rd"] = "road", ["road"] = "road",
            ["ave"] = "avenue", ["av"] = "avenue", ["avenue"] = "avenue",
            ["blvd"] = "boulevard", ["blv"] = "boulevard", ["boulevard"] = "boulevard",
            ["dr"] = "drive", ["drive"] = "drive",
            ["ln"] = "lane", ["lane"] = "lane",
            ["ct"] = "court", ["court"] = "court",
            ["pl"] = "place", ["place"] = "place",
            ["sq"] = "square", ["square"] = "square",
            ["ter"] = "terrace", ["terr"] = "terrace", ["terrace"] = "terrace",
            ["pkwy"] = "parkway", ["pky"] = "parkway", ["parkway"] = "parkway",
            ["hwy"] = "highway", ["highway"] = "highway",
            ["cir"] = "circle", ["circle"] = "circle",
            ["n"] = "north", ["north"] = "north",
            ["s"] = "south", ["south"] = "south",
            ["e"] = "east", ["east"] = "east",
            ["w"] = "west", ["west"] = "west",
            ["ne"] = "northeast", ["northeast"] = "northeast",
            ["nw"] = "northwest", ["northwest"] = "northwest",
            ["se"] = "southeast", ["southeast"] = "southeast",
            ["sw"] = "southwest", ["southwest"] = "southwest",
            ["apt"] = "apartment", ["apartment"] = "apartment",
            ["ste"] = "suite", ["suite"] = "suite",
            ["bldg"] = "building", ["building"] = "building",
            ["fl"] = "floor", ["floor"] = "floor",
            ["rm"] = "room", ["room"] = "room",
        };

    /// <summary>
    /// The canonical tokens <see cref="AddressEquivalents"/> maps onto — the right-hand column,
    /// deduplicated. A token that is one of these came from an abbreviation table rather than from
    /// the address itself, which is what <see cref="MostSelectiveToken"/> has to exclude.
    /// </summary>
    private static readonly HashSet<string> AddressClasses =
        AddressEquivalents.Values.ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// Lower-cases, trims, and collapses every run of whitespace to one space. What "case-insensitive
    /// and forgiving about spacing" means everywhere else in this file.
    /// </summary>
    public static string Normalise(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(text.Length);
        var pendingSpace = false;

        foreach (var character in text)
        {
            if (char.IsWhiteSpace(character))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }

            builder.Append(char.ToLowerInvariant(character));
        }

        return builder.ToString();
    }

    /// <summary>The digits of <paramref name="text"/> and nothing else — how two telephone numbers are compared.</summary>
    /// <remarks>
    /// Keeps digits rather than removing <see cref="PhonePunctuation"/>, so a character nobody
    /// thought of falls out too. The SQL side can only remove what it is told to; that asymmetry is
    /// why the two are tested together rather than reasoned about.
    /// </remarks>
    public static string Digits(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(text.Length);

        foreach (var character in text)
        {
            if (char.IsAsciiDigit(character))
            {
                builder.Append(character);
            }
        }

        return builder.ToString();
    }

    /// <summary>
    /// The comparable form of an address: normalised, stripped of punctuation, and with every token
    /// replaced by its <see cref="AddressEquivalents"/> class.
    /// </summary>
    public static string NormaliseAddress(string? address) => string.Join(' ', AddressTokens(address));

    /// <summary>
    /// The tokens <see cref="NormaliseAddress"/> joins — each already mapped to its equivalence
    /// class. Exposed because <see cref="MostSelectiveToken"/> needs the unjoined form.
    /// </summary>
    public static IReadOnlyList<string> AddressTokens(string? address)
    {
        var normalised = Normalise(address);

        if (normalised.Length is 0)
        {
            return [];
        }

        var tokens = new List<string>();
        var current = new StringBuilder();

        // Anything that is not a letter or a digit separates tokens, which folds commas, full stops
        // and the "#" in "#4" into the same treatment as a space.
        foreach (var character in normalised)
        {
            if (char.IsLetterOrDigit(character))
            {
                current.Append(character);
                continue;
            }

            Flush(tokens, current);
        }

        Flush(tokens, current);

        return tokens;

        static void Flush(List<string> tokens, StringBuilder current)
        {
            if (current.Length is 0)
            {
                return;
            }

            var token = current.ToString();
            current.Clear();

            tokens.Add(AddressEquivalents.TryGetValue(token, out var canonical) ? canonical : token);
        }
    }

    /// <summary>
    /// The token from <paramref name="address"/> most worth narrowing a SQL query by, or an empty
    /// string when there is nothing usable.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The join between the two stages. The candidate query runs against the <b>stored</b> address
    /// columns, which are not normalised, so it can only be narrowed by a token that survives
    /// normalisation unchanged — which rules out every abbreviation in
    /// <see cref="AddressEquivalents"/> (the typed "St" is stored as "Street", or the other way
    /// about) and every bare number (short, and shared by half the street).
    /// </para>
    /// <para>
    /// So: the longest token that is neither an equivalence-class member nor all digits, falling back
    /// to the longest that is merely not a class member — a house number narrows badly but it does at
    /// least appear in the stored column, and the second stage re-checks every candidate properly
    /// anyway. A class member can never be the fallback: "St" normalises to "street", which is a word
    /// the stored address may well not contain at all.
    /// </para>
    /// <para>
    /// An address of nothing but abbreviations therefore has no token, and the caller looks for no
    /// addresses rather than for every premise whose street type is the one that was typed.
    /// </para>
    /// </remarks>
    public static string MostSelectiveToken(string? address)
    {
        var tokens = AddressTokens(address);

        if (tokens.Count is 0)
        {
            return string.Empty;
        }

        var usable = tokens
            .Where(token => !AddressClasses.Contains(token))
            .OrderByDescending(token => !token.All(char.IsAsciiDigit))
            .ThenByDescending(token => token.Length)
            .ThenBy(token => token, StringComparer.Ordinal)
            .FirstOrDefault();

        return usable ?? string.Empty;
    }
}
