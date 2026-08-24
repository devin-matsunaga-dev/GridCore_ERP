import { ChevronUp, LogOut, Moon, Monitor, RefreshCw, Sun, TriangleAlert } from 'lucide-react';
import { useAuth } from 'react-oidc-context';
import { useCurrentUser } from '@/api/identity';
import {
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuLabel,
  DropdownMenuSeparator,
  DropdownMenuTrigger,
} from '@/components/ui/dropdown-menu';
import { Skeleton } from '@/components/ui/skeleton';
import { useTheme, type Theme } from '@/theme/theme-provider';

/** Two letters from a display name; falls back to the first character of whatever we have. */
export function initialsOf(name: string): string {
  const parts = name.trim().split(/\s+/).filter(Boolean);
  if (parts.length === 0) return '?';
  const letters = parts.length === 1 ? parts[0]!.slice(0, 2) : `${parts[0]![0]}${parts.at(-1)![0]}`;
  return letters.toUpperCase();
}

/** The highest-ranking role reads as the person's title in the reference dashboard. */
const roleTitles: Record<string, string> = {
  Administrator: 'Administrator',
  Manager: 'Manager',
  Supervisor: 'Operations Supervisor',
  Finance: 'Finance Analyst',
  Billing: 'Billing Specialist',
  Warehouse: 'Warehouse Lead',
  Technician: 'Field Technician',
  CustomerService: 'Customer Service',
};

const rolePrecedence = Object.keys(roleTitles);

export function primaryRoleTitle(roles: readonly string[]): string {
  const best = rolePrecedence.find((role) => roles.includes(role));
  return best ? roleTitles[best]! : 'GridCore user';
}

const themeOptions: { value: Theme; label: string; icon: typeof Sun }[] = [
  { value: 'light', label: 'Light', icon: Sun },
  { value: 'dark', label: 'Dark', icon: Moon },
  { value: 'system', label: 'System', icon: Monitor },
];

/** Initials once we know who this is; a neutral placeholder until then, never invented letters. */
function Avatar({
  isPending,
  isError,
  displayName,
}: {
  isPending: boolean;
  isError: boolean;
  displayName: string;
}) {
  if (isPending) {
    return <Skeleton className="bg-sidebar-active size-9 shrink-0 rounded-full" />;
  }

  if (isError) {
    return (
      <span className="bg-danger-soft flex size-9 shrink-0 items-center justify-center rounded-full">
        <TriangleAlert className="text-danger size-4" strokeWidth={1.75} aria-hidden="true" />
      </span>
    );
  }

  return (
    <span className="bg-primary flex size-9 shrink-0 items-center justify-center rounded-full text-[13px] font-semibold text-white">
      {initialsOf(displayName)}
    </span>
  );
}

/**
 * Pinned to the bottom of the sidebar: avatar, name, role, and the account menu.
 *
 * The menu is always reachable. `/api/me` can be slow, fail, or answer with a token carrying no
 * name, and in every one of those cases the person still has to be able to sign out — so the
 * loading and error states fill in the *text*, never replace the button.
 */
export function UserCard() {
  const { data, isPending, isError, refetch } = useCurrentUser();
  const auth = useAuth();
  const { theme, setTheme } = useTheme();

  const displayName = data?.userName ?? data?.email ?? (isError ? 'Account' : 'Signed-in user');
  const roleTitle = isError ? 'Details unavailable' : primaryRoleTitle(data?.roles ?? []);

  return (
    <div className="border-sidebar-border border-t p-3">
      <DropdownMenu>
        <DropdownMenuTrigger
          className="hover:bg-sidebar-active rounded-control flex w-full items-center gap-3 px-2 py-1.5 text-left transition-colors"
          aria-label="Account menu"
        >
          <Avatar isPending={isPending} isError={isError} displayName={displayName} />

          <span className="min-w-0 flex-1">
            {isPending ? (
              <span className="block space-y-1.5 py-0.5">
                <Skeleton className="bg-sidebar-active h-3 w-24" />
                <Skeleton className="bg-sidebar-active h-2.5 w-16" />
              </span>
            ) : (
              <>
                <span className="block truncate text-sm font-semibold text-white">{displayName}</span>
                <span className="text-sidebar-text block truncate text-xs">{roleTitle}</span>
              </>
            )}
          </span>

          <ChevronUp className="text-sidebar-text size-4 shrink-0" strokeWidth={1.75} aria-hidden="true" />
        </DropdownMenuTrigger>

        <DropdownMenuContent align="start" side="top" className="w-56">
          <DropdownMenuLabel>{data?.email ?? displayName}</DropdownMenuLabel>

          {isError && (
            <>
              <DropdownMenuSeparator />
              <DropdownMenuItem onSelect={() => void refetch()}>
                <RefreshCw aria-hidden="true" />
                Retry loading account
              </DropdownMenuItem>
            </>
          )}

          <DropdownMenuSeparator />
          <DropdownMenuLabel className="pb-1 text-[11px] tracking-wide uppercase">Theme</DropdownMenuLabel>
          {themeOptions.map((option) => (
            <DropdownMenuItem
              key={option.value}
              onSelect={() => setTheme(option.value)}
              className={theme === option.value ? 'text-primary font-medium' : undefined}
            >
              <option.icon aria-hidden="true" />
              {option.label}
            </DropdownMenuItem>
          ))}

          <DropdownMenuSeparator />
          <DropdownMenuItem onSelect={() => void auth.signoutRedirect()} className="text-danger focus:text-danger">
            <LogOut aria-hidden="true" />
            Sign out
          </DropdownMenuItem>
        </DropdownMenuContent>
      </DropdownMenu>
    </div>
  );
}
