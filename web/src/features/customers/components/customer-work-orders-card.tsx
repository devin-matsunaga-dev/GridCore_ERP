import { Wrench } from 'lucide-react';
import { EmptyState } from '@/components/registry/empty-state';
import { Card, CardHeader, CardTitle } from '@/components/ui/card';

/**
 * The work raised against this customer's premises.
 *
 * **THERE IS NO DATA BEHIND THIS PANEL YET, AND THE EMPTY STATE SAYS SO IN THOSE WORDS.**
 * `Modules.WorkOrders` is still a bare `IModule` — no entities, no endpoints, nothing to ask — and
 * Phase 3 (the Ops & Maintenance cycle) is what builds it. So this panel deliberately **issues no
 * request**: there is no route to call, and a fetch against one would be a 404 rendered as a
 * failure, which reads as something broken rather than as something not built.
 *
 * It is here rather than deferred because the tab is the seam. When Phase 3 gives work orders a
 * register, wiring this panel is a `useWorkOrders({ customerId })` and a `RegistryTableCard` beside
 * the three already on this page — the tab, its route and its timeline slot are where they belong
 * already. That is the same call WP-1.5 made when it rendered the state machines' transitions as
 * read-only pills.
 *
 * The wording matters as much as the panel: WP-2.9's lesson was that a surface claiming more than
 * it does is worse than no surface. "Nothing open" would be a claim about the customer; "not built
 * yet" is a claim about the product, and only one of them is true today.
 */
export function CustomerWorkOrdersCard() {
  return (
    <Card>
      <CardHeader>
        <CardTitle>Open work orders</CardTitle>
      </CardHeader>

      <EmptyState
        icon={Wrench}
        title="Work orders are not built yet"
        message="Jobs raised against this customer's premises will appear here once the work-order register exists. Nothing is being hidden — there is no register to ask."
      />
    </Card>
  );
}
