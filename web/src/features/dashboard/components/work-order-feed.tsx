import { EllipsisVertical, ListFilter } from 'lucide-react';
import { useState } from 'react';
import { Link } from 'react-router';
import { Button } from '@/components/ui/button';
import { Card, CardAction, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card';
import { Pagination, pageCount } from '@/components/ui/pagination';
import type { WorkOrderRow } from '../demo-data';
import { WorkOrderTable } from './work-order-table';

const defaultPageSize = 5;

/** Slices a page out of a row list, clamping the page so a shrinking list cannot land past the end. */
export function pageOf<T>(rows: readonly T[], page: number, pageSize: number): T[] {
  const clamped = Math.min(Math.max(page, 1), pageCount(rows.length, pageSize));

  return rows.slice((clamped - 1) * pageSize, clamped * pageSize) as T[];
}

export function WorkOrderFeed({ rows }: { rows: WorkOrderRow[] }) {
  const [page, setPage] = useState(1);
  const [pageSize, setPageSize] = useState<number>(defaultPageSize);

  return (
    <Card className="flex flex-col">
      <CardHeader>
        <div>
          <CardTitle>Work Order Feed</CardTitle>
          <CardDescription>Latest activity</CardDescription>
        </div>
        <CardAction>
          <Button variant="secondary" size="sm" className="text-[13px]">
            <ListFilter className="size-4" aria-hidden="true" />
            Filters
          </Button>
          <Link to="/work-orders" className="text-primary text-[13px] font-medium hover:underline">
            View all
          </Link>
          <Button variant="ghost" size="iconSm" aria-label="Work order feed options">
            <EllipsisVertical className="size-4" aria-hidden="true" />
          </Button>
        </CardAction>
      </CardHeader>

      <CardContent className="flex-1">
        <WorkOrderTable rows={pageOf(rows, page, pageSize)} />
      </CardContent>

      <div className="px-6 pb-5">
        <Pagination
          page={page}
          pageSize={pageSize}
          totalRows={rows.length}
          onPageChange={setPage}
          onPageSizeChange={(size) => {
            setPageSize(size);
            // The old page number may not exist at the new size; the first page always does.
            setPage(1);
          }}
        />
      </div>
    </Card>
  );
}
