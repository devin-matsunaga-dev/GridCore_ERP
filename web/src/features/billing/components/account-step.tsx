import { useMutation } from '@tanstack/react-query';
import { customersApi, type Customer, type ServiceAccount, type ServiceLocation } from '@/api/customers';
import { toast } from '@/components/feedback/toast';
import { Button } from '@/components/ui/button';
import { formatDateTime } from '@/lib/format';
import { StepFacts } from './step-card';

/**
 * SPEC step 2 — Create Service Account.
 *
 * Opening the account and energising it are **two** calls, deliberately kept as two: an account
 * that was opened and never energised is a real state, and it is one the billing run refuses by
 * name ("has never been energised") because nothing was supplied under it. Pressing one button
 * performs both, and the facts report the transition so it is visible that both happened.
 */

export function AccountStep({
  customer,
  location,
  result,
  onDone,
}: {
  customer: Customer;
  location: ServiceLocation;
  result?: ServiceAccount;
  onDone: (account: ServiceAccount) => void;
}) {
  const open = useMutation({
    mutationFn: async () => {
      const opened = await customersApi.openAccount({
        customerId: customer.id,
        serviceLocationId: location.id,

        // The demonstration walk is the revenue cycle — a meter, a reading and a bill — so the
        // account it opens is an electricity one. Stated rather than defaulted (WP-2.17): the
        // service is what the deposit and the tariff both key on.
        serviceType: 'Electricity',
        reason: 'Requested at the counter',
      });

      return customersApi.startService(opened.id, 'Meter energised');
    },
    onSuccess: (account) => {
      toast.success(`Account ${account.accountNumber} energised`, 'Pending → Active');
      onDone(account);
    },
    onError: (error) => toast.apiError(error, 'The service account could not be opened.'),
  });

  if (result) {
    return (
      <StepFacts
        facts={[
          { label: 'Account number', value: result.accountNumber },
          { label: 'Status', value: result.status },
          { label: 'Opened', value: formatDateTime(result.openedAt) },
          {
            label: 'Energised',
            value: result.serviceStartedAt ? formatDateTime(result.serviceStartedAt) : null,
          },
          { label: 'Premise', value: location.formattedAddress },
        ]}
      />
    );
  }

  return (
    <Button onClick={() => open.mutate()} disabled={open.isPending}>
      {open.isPending ? 'Opening…' : 'Open and energise the account'}
    </Button>
  );
}
