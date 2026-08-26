import { Pin } from 'lucide-react';
import { Link } from 'react-router';
import type { CustomerNote } from '@/api/customers';
import { Card, CardContent } from '@/components/ui/card';
import { Skeleton } from '@/components/ui/skeleton';
import { StatusPill } from '@/components/ui/status';
import { formatDate } from '@/lib/format';
import { noteKindLabel, noteKindTone, pinnedNotes } from '../notes';

/**
 * The notes somebody put at the top of this customer's log, on the summary.
 *
 * WORK_PACKAGES.md WP-2.13: "pinned notes surface at the top of the 360". This is the surfacing —
 * a standing instruction is only worth pinning if a rep meets it without going looking, and the
 * notes tab is one click too many for "there is a dog on the property".
 *
 * **Renders nothing at all when there is nothing pinned**, rather than an empty state. An empty
 * state here would be a permanent block of furniture on the page every customer sees, explaining a
 * feature to a rep who is trying to read a balance. The notes tab has the empty state, where
 * somebody has already asked the question.
 */
export function CustomerPinnedNotes({
  notes,
  isLoading,
}: {
  notes: readonly CustomerNote[];
  isLoading: boolean;
}) {
  // No error branch either, and deliberately: the notes tab and the timeline both report a failed
  // note fetch in their own words. A third complaint about it, wedged above the customer record on
  // the summary, would be noise on the page a rep opens most.
  if (isLoading) {
    return (
      <Card>
        <CardContent className="space-y-2 pt-6">
          <Skeleton className="h-4 w-40" />
          <Skeleton className="h-4 w-2/3" />
        </CardContent>
      </Card>
    );
  }

  const pinned = pinnedNotes(notes);

  if (pinned.length === 0) return null;

  return (
    <Card>
      <CardContent className="space-y-3 pt-6">
        <div className="text-muted flex items-center gap-1.5 text-[13px] font-medium">
          <Pin aria-hidden="true" className="size-4" />
          <span>Pinned notes</span>
        </div>

        <ul className="space-y-3">
          {pinned.map((note) => (
            <li key={note.id} className="flex flex-wrap items-start gap-2">
              <StatusPill status={noteKindLabel(note.kind)} tone={noteKindTone(note.kind)} />

              <div className="min-w-0 flex-1">
                <p className="text-body text-sm">{note.body}</p>
                <p className="text-muted mt-0.5 text-xs">
                  {note.actorName ?? note.actorId} · {formatDate(note.recordedAt)}
                </p>
              </div>
            </li>
          ))}
        </ul>

        {/*
          Relative, so this card does not have to know the customer's id — the tab it points at is a
          sibling route of the summary, which is what makes `../notes` correct from `/customers/{id}`.
        */}
        <Link to="notes" className="text-primary inline-block text-[13px] hover:underline">
          All notes and interactions
        </Link>
      </CardContent>
    </Card>
  );
}
