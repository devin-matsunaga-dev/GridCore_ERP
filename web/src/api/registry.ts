/**
 * What the registry list endpoints will hand back, and what they will not.
 *
 * Every list endpoint in the Customers, Assets and Inventory modules takes its filters as query
 * parameters and answers with a plain array clamped to `MaxPageSize` — no offset, no cursor and no
 * total count. So a screen filters on the server (which is what keeps a search off the client) and
 * sorts and pages within the window it got back. When that window comes back full there may be
 * more rows behind it, and the table says so rather than quietly showing a truncated list as if it
 * were the whole registry.
 */

/** The largest page any registry service will return — `CustomerService.MaxPageSize` and its peers. */
export const registryWindow = 200;

/**
 * True when the answer filled the window, so rows may have been cut off the end of it. Takes the
 * count rather than the rows, because that is all a screen has by the time it has sorted and paged.
 */
export function isWindowFull(returnedRows: number | undefined): boolean {
  return returnedRows !== undefined && returnedRows >= registryWindow;
}
