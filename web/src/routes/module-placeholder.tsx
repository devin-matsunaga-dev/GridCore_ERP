import { ArrowLeft, type LucideIcon } from 'lucide-react';
import { Link } from 'react-router';
import { Button } from '@/components/ui/button';
import { Card } from '@/components/ui/card';

export type ModulePlaceholderProps = {
  title: string;
  icon: LucideIcon;
  /** The work package that fills this area in, so the shell says what is coming rather than "TODO". */
  owner: string;
};

/** DESIGN.md empty state: icon + message + action, never a blank canvas. */
export function ModulePlaceholder({ title, icon: Icon, owner }: ModulePlaceholderProps) {
  return (
    <div className="space-y-6">
      <div>
        <h2 className="text-heading text-[26px] leading-tight font-bold">{title}</h2>
        <p className="text-muted mt-0.5 text-sm">This area of GridCore is not built yet.</p>
      </div>

      <Card className="flex flex-col items-center justify-center px-6 py-20 text-center">
        <span className="bg-primary-soft flex size-14 items-center justify-center rounded-full">
          <Icon className="text-primary size-7" strokeWidth={1.5} aria-hidden="true" />
        </span>
        <h3 className="text-heading mt-5 text-base font-semibold">Nothing here yet</h3>
        <p className="text-body mt-1.5 max-w-sm text-sm">
          {title} ships with {owner}. The shell, navigation and design system are in place, so the screens
          drop straight in.
        </p>
        <Button variant="secondary" className="mt-6" asChild>
          <Link to="/">
            <ArrowLeft aria-hidden="true" />
            Back to home
          </Link>
        </Button>
      </Card>
    </div>
  );
}
