import { ArrowLeft, MapPinOff } from 'lucide-react';
import { Link } from 'react-router';
import { Button } from '@/components/ui/button';
import { Card } from '@/components/ui/card';

export function NotFoundPage() {
  return (
    <Card className="flex flex-col items-center justify-center px-6 py-20 text-center">
      <span className="bg-neutral-soft flex size-14 items-center justify-center rounded-full">
        <MapPinOff className="text-muted size-7" strokeWidth={1.5} aria-hidden="true" />
      </span>
      <h2 className="text-heading mt-5 text-xl font-bold">Page not found</h2>
      <p className="text-body mt-1.5 max-w-sm text-sm">
        That address does not match anything in GridCore.
      </p>
      <Button variant="secondary" className="mt-6" asChild>
        <Link to="/">
          <ArrowLeft aria-hidden="true" />
          Back to home
        </Link>
      </Button>
    </Card>
  );
}
