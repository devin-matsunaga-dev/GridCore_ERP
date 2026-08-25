using System.Linq.Expressions;
using GridCore.Contracts.Directories;
using GridCore.Modules.Customers.Data;
using GridCore.Modules.Customers.Features.Customers;
using GridCore.Modules.Customers.Features.ServiceAccounts;
using GridCore.Modules.Customers.Features.ServiceLocations;
using Microsoft.EntityFrameworkCore;

namespace GridCore.Modules.Customers.Features.Search;

/// <summary>What a rep asked the search box for.</summary>
/// <param name="Term">What they typed. Blank is not an error — it is the resting state of the box.</param>
/// <param name="Status">Only customers in this status, as the registry's own filter means it.</param>
/// <param name="Class">Only customers of this class.</param>
/// <param name="Page">Which page of results, one-based.</param>
/// <param name="PageSize">How many rows on it.</param>
/// <remarks>
/// The two filters are here because the search box <i>is</i> the registry's search box: one field
/// beside the status and class selects, and a search that ignored the selects beside it would answer
/// a question nobody asked. They narrow the customers every candidate query runs against, so they
/// apply to an address or meter match exactly as they do to a name — a filter that only reached
/// three of the five kinds would be worse than none.
/// </remarks>
public sealed record CustomerSearchQuery(
    string? Term = null,
    CustomerStatus? Status = null,
    CustomerClass? Class = null,
    int Page = 1,
    int PageSize = CustomerSearchQuery.DefaultPageSize)
{
    /// <summary>Rows on a page when the caller does not say.</summary>
    public const int DefaultPageSize = 20;

    /// <summary>
    /// The largest page that will be answered, whatever the caller asks for — the same window
    /// <c>CustomerService.MaxPageSize</c> caps the registry list at, because the SPA asks this
    /// endpoint for one window and sorts and pages it in the browser exactly as it does that list.
    /// </summary>
    public const int MaxPageSize = CustomerService.MaxPageSize;

    /// <summary>The page actually served — one-based, never below one.</summary>
    public int ServedPage => Math.Max(Page, 1);

    /// <summary>The page size actually served.</summary>
    public int ServedPageSize => Math.Clamp(PageSize, 1, MaxPageSize);

    /// <summary>How many ranked rows this page skips.</summary>
    public int Skip => (ServedPage - 1) * ServedPageSize;
}

/// <summary>One row of a search result: a customer, why they matched, and enough context to tell them apart.</summary>
/// <param name="Customer">
/// The whole customer, not a projection of one. The search box is the registry's search box, so a
/// result row and a registry row are the same row — anything less here would mean a table whose
/// columns emptied out the moment somebody typed in the filter above it.
/// </param>
/// <param name="MatchedOn">Which field matched — the label the row carries.</param>
/// <param name="IsExact">Whether the whole field matched rather than part of it.</param>
/// <param name="MatchedValue">The stored value that matched, as stored.</param>
/// <param name="ServiceAccountCount">How many accounts they hold that are not closed.</param>
/// <param name="ServiceAccountNumber">The account the row is about, when there is exactly one it could be.</param>
/// <param name="ServiceAddress">Where that account is served, when there is exactly one it could be.</param>
/// <param name="MeterNumber">The meter the match came through, for a meter-number match.</param>
public sealed record CustomerSearchHit(
    Customer Customer,
    CustomerMatchKind MatchedOn,
    bool IsExact,
    string MatchedValue,
    int ServiceAccountCount,
    string? ServiceAccountNumber,
    string? ServiceAddress,
    string? MeterNumber);

/// <summary>A page of search results, and what the search made of the term it was given.</summary>
/// <param name="Term">What was typed, as typed — so "no matches for …" can quote it.</param>
/// <param name="Kinds">The kinds the term was dispatched as, in precedence order.</param>
/// <param name="Hits">This page of rows, best first.</param>
/// <param name="Total">Matching customers across every page.</param>
/// <param name="Page">Which page this is, one-based.</param>
/// <param name="PageSize">How many rows a full page holds.</param>
/// <param name="Truncated">
/// Whether a candidate cap was reached, so <paramref name="Total"/> is a floor rather than a count.
/// Reported rather than hidden: a rep who typed something far too broad should be told that the
/// answer is incomplete, not shown a confident wrong number.
/// </param>
public sealed record CustomerSearchResult(
    string Term,
    IReadOnlyList<CustomerMatchKind> Kinds,
    IReadOnlyList<CustomerSearchHit> Hits,
    int Total,
    int Page,
    int PageSize,
    bool Truncated)
{
    /// <summary>The answer to a term there was nothing to look for in.</summary>
    public static CustomerSearchResult Empty(CustomerSearchTerm term, CustomerSearchQuery query) =>
        new(term.Raw, term.Kinds, [], 0, query.ServedPage, query.ServedPageSize, false);
}

/// <summary>The lookup a rep uses fifty times a day: one box across five ways of naming a customer.</summary>
public interface ICustomerSearchService
{
    /// <summary>Searches, ranks and pages. A blank term answers with no rows rather than throwing.</summary>
    Task<CustomerSearchResult> SearchAsync(CustomerSearchQuery query, CancellationToken cancellationToken = default);
}

/// <summary>
/// CSR customer search, in two stages: a bounded SQL pass that gathers candidates, then the pure
/// functions in <see cref="SearchText"/> and <see cref="CustomerSearchRanking"/> that decide what
/// actually matched and in what order.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why two stages.</b> The comparisons this search promises — a telephone number with its
/// punctuation ignored, an address where "St" and "Street" are the same word — are not comparisons
/// SQL can make against the stored columns, and making them possible would mean storing a second,
/// normalised copy of every address and every phone number, kept in step by a trigger or by every
/// write path remembering to. So SQL does what it is good at (narrow to a bounded candidate set
/// using the columns as stored) and C# does the rest (normalise both sides, decide exactness, rank).
/// A stored normalised column is the door out of this when the register grows enough to need it —
/// deliberately not opened yet (owner's call).
/// </para>
/// <para>
/// <b>Every kind probes for an exact match before it scans for partial ones,</b> and a probe that
/// hits skips the scan: somebody who quotes a whole account number has told you which account they
/// mean. For account and meter numbers the probe is an index seek
/// (<c>ux_customers_account_number</c>, <c>ux_meters_meter_number</c>), which is what keeps the
/// fifty-times-a-day path cheap however large the register gets. Name and phone have no such index
/// and their probe is an equality scan; the address kind has no probe at all, because the
/// comparison it needs is the one that does not exist in SQL.
/// </para>
/// <para>
/// <b>Paging is over the ranked list, on the server.</b> Ranking is a whole-result-set operation —
/// the best answer for a customer may be found by the last query of the five — so candidates are
/// gathered, reduced to one row per customer, ordered, and only then cut into a page. That is what
/// <see cref="CandidateLimit"/> bounds, and what <c>Truncated</c> reports when it bites.
/// </para>
/// <para>
/// This module reads the meter register through <see cref="IMeterDirectory"/> and has never heard of
/// a <c>metering</c> schema. Metering could not answer "whose meter is this" without reading the
/// customers schema, which is why the resolution is two hops with the boundary in the middle.
/// </para>
/// </remarks>
public sealed class CustomerSearchService(CustomersDbContext database, IMeterDirectory meters) : ICustomerSearchService
{
    /// <summary>
    /// Most candidates any one kind contributes before the answer is declared truncated.
    /// </summary>
    /// <remarks>
    /// The bound on the whole search: five kinds, each capped, so the worst case a badly chosen term
    /// can cost is fixed and small. Generous enough that a real lookup never reaches it — a rep
    /// typing something that matches two hundred customers is not looking for one of them.
    /// </remarks>
    public const int CandidateLimit = 200;

    /// <inheritdoc />
    public async Task<CustomerSearchResult> SearchAsync(CustomerSearchQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var term = CustomerSearchTerm.Classify(query.Term);

        if (term.IsEmpty)
        {
            return CustomerSearchResult.Empty(term, query);
        }

        // Built once and threaded through every candidate query, so the status and class selects
        // beside the box narrow all five kinds rather than the three that read the customer table
        // directly.
        var customers = Filtered(query);

        var gathered = new List<CustomerSearchCandidate>();
        var truncated = false;

        foreach (var kind in term.Kinds)
        {
            var candidates = await GatherAsync(kind, term, customers, cancellationToken).ConfigureAwait(false);

            gathered.AddRange(candidates.Found);
            truncated |= candidates.Truncated;
        }

        var ranked = CustomerSearchRanking.Rank(gathered);
        var page = ranked.Skip(query.Skip).Take(query.ServedPageSize).ToList();

        return new CustomerSearchResult(
            term.Raw,
            term.Kinds,
            await DescribeAsync(page, customers, cancellationToken).ConfigureAwait(false),
            ranked.Count,
            query.ServedPage,
            query.ServedPageSize,
            truncated);
    }

    private Task<Candidates> GatherAsync(
        CustomerMatchKind kind,
        CustomerSearchTerm term,
        IQueryable<Customer> customers,
        CancellationToken cancellationToken) =>
        kind switch
        {
            CustomerMatchKind.AccountNumber => ByAccountNumberAsync(term, customers, cancellationToken),
            CustomerMatchKind.MeterNumber => ByMeterNumberAsync(term, customers, cancellationToken),
            CustomerMatchKind.Phone => ByPhoneAsync(term, customers, cancellationToken),
            CustomerMatchKind.Name => ByNameAsync(term, customers, cancellationToken),
            CustomerMatchKind.Address => ByAddressAsync(term, customers, cancellationToken),

            // Never reached through Classify, and a throw rather than an empty set: a kind added to
            // the enum without a gatherer is a bug, and one that would show up as a search that
            // quietly stopped finding people.
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "No candidate query for this match kind."),
        };

    private async Task<Candidates> ByAccountNumberAsync(
        CustomerSearchTerm term,
        IQueryable<Customer> customers,
        CancellationToken cancellationToken)
    {
        // Equality, so this is a seek on ux_customers_account_number. Lower-cased on both sides
        // rather than ILIKE, which is Npgsql-only and would leave the fast tier testing other SQL.
        var exact = await customers
            .Where(customer => customer.AccountNumber.ToLower() == term.Normalised)
            .Select(customer => new IdentifierRow(customer.Id, customer.AccountNumber))
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (exact is not null)
        {
            return Candidates.Of([Candidate(exact, CustomerMatchKind.AccountNumber, isExact: true)]);
        }

        var partial = await customers
            .Where(customer => customer.AccountNumber.ToLower().Contains(term.Normalised))
            .OrderBy(customer => customer.AccountNumber)
            .Take(CandidateLimit)
            .Select(customer => new IdentifierRow(customer.Id, customer.AccountNumber))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return Candidates.Of(
            partial.Select(row => Candidate(row, CustomerMatchKind.AccountNumber, isExact: false)).ToList(),
            partial.Count);
    }

    private async Task<Candidates> ByNameAsync(
        CustomerSearchTerm term,
        IQueryable<Customer> customers,
        CancellationToken cancellationToken)
    {
        // A name is not unique — two households share one often enough — so the exact probe returns
        // a list where the account-number one returns at most a row.
        var exact = await customers
            .Where(customer => customer.Name.ToLower() == term.Normalised)
            .OrderBy(customer => customer.Name)
            .Take(CandidateLimit)
            .Select(customer => new IdentifierRow(customer.Id, customer.Name))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (exact.Count > 0)
        {
            return Candidates.Of(
                exact.Select(row => Candidate(row, CustomerMatchKind.Name, isExact: true)).ToList(),
                exact.Count);
        }

        var partial = await customers
            .Where(customer => customer.Name.ToLower().Contains(term.Normalised))
            .OrderBy(customer => customer.Name)
            .Take(CandidateLimit)
            .Select(customer => new IdentifierRow(customer.Id, customer.Name))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return Candidates.Of(
            partial.Select(row => Candidate(row, CustomerMatchKind.Name, isExact: false)).ToList(),
            partial.Count);
    }

    private async Task<Candidates> ByPhoneAsync(
        CustomerSearchTerm term,
        IQueryable<Customer> customers,
        CancellationToken cancellationToken)
    {
        var digits = term.Digits;

        if (digits.Length is 0)
        {
            return Candidates.None;
        }

        // The stored column carries whatever was typed at the counter — "(670) 285-1234", "670.285.1234",
        // "+1 670 285 1234" — so the punctuation comes off in SQL before the comparison. A chain of
        // Replace calls, because that is what both Npgsql and SQLite translate into nested replace().
        //
        // Written out inline, twice, rather than extracted to a helper: this is an expression tree,
        // and EF cannot translate a call to a method of ours however small it is — the first attempt
        // at a helper compiled and threw at run time. The characters removed are
        // SearchText.PhonePunctuation, and SearchText.Digits is the same operation in C#;
        // CustomerSearchServiceTests runs punctuated numbers through both halves rather than trusting
        // two lists in two languages to agree.
        var exact = await customers
            .Where(customer => customer.Phone != null
                && customer.Phone!
                    .Replace("(", "")
                    .Replace(")", "")
                    .Replace("-", "")
                    .Replace(" ", "")
                    .Replace(".", "")
                    .Replace("+", "")
                    .Replace("/", "") == digits)
            .OrderBy(customer => customer.Name)
            .Take(CandidateLimit)
            .Select(customer => new IdentifierRow(customer.Id, customer.Phone!))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (exact.Count > 0)
        {
            return Candidates.Of(
                exact.Select(row => Candidate(row, CustomerMatchKind.Phone, isExact: true)).ToList(),
                exact.Count);
        }

        var partial = await customers
            .Where(customer => customer.Phone != null
                && customer.Phone!
                    .Replace("(", "")
                    .Replace(")", "")
                    .Replace("-", "")
                    .Replace(" ", "")
                    .Replace(".", "")
                    .Replace("+", "")
                    .Replace("/", "").Contains(digits))
            .OrderBy(customer => customer.Name)
            .Take(CandidateLimit)
            .Select(customer => new IdentifierRow(customer.Id, customer.Phone!))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return Candidates.Of(
            partial.Select(row => Candidate(row, CustomerMatchKind.Phone, isExact: false)).ToList(),
            partial.Count);
    }

    private async Task<Candidates> ByMeterNumberAsync(
        CustomerSearchTerm term,
        IQueryable<Customer> customers,
        CancellationToken cancellationToken)
    {
        var exact = await meters.FindByNumberAsync(term.Normalised, cancellationToken).ConfigureAwait(false);

        IReadOnlyList<MeterSummary> found = exact is not null
            ? [exact]
            : await meters.SearchByNumberAsync(term.Normalised, CandidateLimit, cancellationToken).ConfigureAwait(false);

        // Keyed by premise. At most one fitted meter stands at a premise —
        // ux_meters_service_location makes it a database fact — and an unfitted meter measures
        // nobody, so it names no customer and drops out here.
        var byPremise = found
            .Where(meter => meter.ServiceLocationId is not null)
            .ToDictionary(meter => meter.ServiceLocationId!.Value);

        var truncated = exact is null && found.Count >= CandidateLimit;

        if (byPremise.Count is 0)
        {
            return new Candidates([], truncated);
        }

        var premises = byPremise.Keys.ToArray();

        var served = await ServedPremises(customers, location => premises.Contains(location.Id), _ => true)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var candidates = served
            .Select(row =>
            {
                var meter = byPremise[row.Location.Id];

                return new CustomerSearchCandidate(
                    row.CustomerId,
                    CustomerMatchKind.MeterNumber,
                    string.Equals(meter.MeterNumber, term.Normalised, StringComparison.OrdinalIgnoreCase),
                    meter.MeterNumber,
                    row.ServiceAccountNumber,
                    row.Location.Address.OneLine,
                    meter.MeterNumber);
            })
            .ToList();

        return new Candidates(candidates, truncated);
    }

    private async Task<Candidates> ByAddressAsync(
        CustomerSearchTerm term,
        IQueryable<Customer> customers,
        CancellationToken cancellationToken)
    {
        var token = term.AddressToken;

        if (token.Length is 0)
        {
            return Candidates.None;
        }

        // Stage one narrows on one token, because the stored columns are not normalised and a token
        // that survives normalisation unchanged is the only thing that can be matched against them.
        // Stage two below is what makes "St" and "Street" the same word.
        var rows = await ServedPremises(
                customers,
                location =>
                    location.Address.Line1.ToLower().Contains(token)
                    || (location.Address.Line2 != null && location.Address.Line2.ToLower().Contains(token))
                    || location.Address.City.ToLower().Contains(token)
                    || location.Address.Region.ToLower().Contains(token),
                _ => true)
            .Take(CandidateLimit)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var candidates = new List<CustomerSearchCandidate>();

        foreach (var row in rows)
        {
            var address = row.Location.Address.OneLine;
            var stored = SearchText.NormaliseAddress(address);

            // The token got them here; the whole typed address is what decides whether they stay.
            if (!stored.Contains(term.NormalisedAddress, StringComparison.Ordinal))
            {
                continue;
            }

            candidates.Add(new CustomerSearchCandidate(
                row.CustomerId,
                CustomerMatchKind.Address,
                string.Equals(stored, term.NormalisedAddress, StringComparison.Ordinal),
                address,
                row.ServiceAccountNumber,
                address));
        }

        return new Candidates(candidates, rows.Count >= CandidateLimit);
    }

    /// <summary>
    /// Fills a ranked page out into rows: the customer's own details, plus the premise context for
    /// the matches that arrived without any.
    /// </summary>
    /// <remarks>
    /// Two queries for a whole page, however it was matched. The context matters because a result
    /// list of three people called J. Cruz is not a search result, it is a second question — and the
    /// count is carried rather than a guess, so a customer with two open accounts shows neither
    /// address instead of arbitrarily showing one.
    /// </remarks>
    private async Task<IReadOnlyList<CustomerSearchHit>> DescribeAsync(
        IReadOnlyList<CustomerSearchCandidate> page,
        IQueryable<Customer> customers,
        CancellationToken cancellationToken)
    {
        if (page.Count is 0)
        {
            return [];
        }

        var ids = page.Select(candidate => candidate.CustomerId).Distinct().ToArray();

        var found = await customers
            .Where(customer => ids.Contains(customer.Id))
            .ToDictionaryAsync(customer => customer.Id, cancellationToken)
            .ConfigureAwait(false);

        // Filtered on the account rather than on the projected row: EF cannot translate a Where
        // applied to a projection into a record, which compiles and throws at run time.
        var context = await ServedPremises(customers, _ => true, account => ids.Contains(account.CustomerId))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var byCustomer = context
            .GroupBy(row => row.CustomerId)
            .ToDictionary(group => group.Key, group => group.ToList());

        var hits = new List<CustomerSearchHit>(page.Count);

        foreach (var candidate in page)
        {
            // A customer that vanished between the candidate query and this one is simply not shown:
            // a search result is a snapshot, and losing a row beats failing the whole page.
            if (!found.TryGetValue(candidate.CustomerId, out var customer))
            {
                continue;
            }

            var served = byCustomer.GetValueOrDefault(candidate.CustomerId) ?? [];
            var only = served.Count is 1 ? served[0] : null;

            hits.Add(new CustomerSearchHit(
                customer,
                candidate.Kind,
                candidate.IsExact,
                candidate.MatchedValue,
                served.Count,
                candidate.ServiceAccountNumber ?? only?.ServiceAccountNumber,
                candidate.ServiceAddress ?? only?.Location.Address.OneLine,
                candidate.MeterNumber));
        }

        return hits;
    }

    /// <summary>
    /// The customers this search may return: every one, untracked, narrowed by whatever the selects
    /// beside the box are set to.
    /// </summary>
    /// <remarks>
    /// Matched against non-nullable locals, as <c>CustomerService.ListAsync</c> does: both columns
    /// are stored by name and EF cannot translate a nullable-to-converted-value comparison.
    /// </remarks>
    private IQueryable<Customer> Filtered(CustomerSearchQuery query)
    {
        var customers = database.Customers.AsNoTracking();

        if (query.Status is { } status)
        {
            customers = customers.Where(customer => customer.Status == status);
        }

        if (query.Class is { } customerClass)
        {
            customers = customers.Where(customer => customer.Class == customerClass);
        }

        return customers;
    }

    /// <summary>
    /// The premises matching <paramref name="premises"/> whose account matches
    /// <paramref name="accounts"/>, each with the account taking service there and the customer
    /// holding it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// "Not closed" rather than a list of the open statuses — the same predicate
    /// <c>ux_service_accounts_open_location</c> filters on, so a status added later joins the set
    /// without this line having to be remembered. A premise nobody is taking service at names no
    /// customer and is therefore not a search result: the address of an empty house is a fact about
    /// the premise register, which is its own screen.
    /// </para>
    /// <para>
    /// Filtered entities first, joined and projected last. EF cannot translate a <c>Where</c>
    /// applied to a projection into a record — it compiles and throws at run time — which is the
    /// same shape the two directories in this module are built in.
    /// </para>
    /// </remarks>
    private IQueryable<PremiseRow> ServedPremises(
        IQueryable<Customer> customers,
        Expression<Func<ServiceLocation, bool>> premises,
        Expression<Func<ServiceAccount, bool>> accounts) =>
        from location in database.ServiceLocations.AsNoTracking().Where(premises)
        join account in database.ServiceAccounts.AsNoTracking()
            .Where(account => account.Status != ServiceAccountStatus.Closed)
            .Where(accounts)
            on location.Id equals account.ServiceLocationId
        join customer in customers on account.CustomerId equals customer.Id
        orderby location.LocationCode
        select new PremiseRow(location, account.AccountNumber, customer.Id);

    private static CustomerSearchCandidate Candidate(IdentifierRow row, CustomerMatchKind kind, bool isExact) =>
        new(row.CustomerId, kind, isExact, row.Value);

    /// <summary>A customer and the one stored value that matched, as the candidate queries project them.</summary>
    private sealed record IdentifierRow(Guid CustomerId, string Value);

    /// <summary>A premise, the account taking service there, and who holds it.</summary>
    private sealed record PremiseRow(ServiceLocation Location, string ServiceAccountNumber, Guid CustomerId);

    /// <summary>What one kind's candidate queries found, and whether they hit the cap doing it.</summary>
    private sealed record Candidates(IReadOnlyList<CustomerSearchCandidate> Found, bool Truncated)
    {
        /// <summary>Nothing found, nothing truncated.</summary>
        public static readonly Candidates None = new([], false);

        /// <summary>What was found, truncated when the query that found it came back full.</summary>
        public static Candidates Of(IReadOnlyList<CustomerSearchCandidate> found, int rowsRead = 0) =>
            new(found, rowsRead >= CandidateLimit);
    }
}
