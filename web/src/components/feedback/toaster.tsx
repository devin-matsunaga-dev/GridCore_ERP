import { Toaster as SonnerToaster } from 'sonner';
import { useTheme } from '@/theme/theme-provider';

/** Mounted once at the root; picks up the app's resolved theme. */
export function Toaster() {
  const { resolvedTheme } = useTheme();

  return (
    <SonnerToaster
      theme={resolvedTheme}
      position="bottom-right"
      richColors
      closeButton
      toastOptions={{ className: 'rounded-control' }}
    />
  );
}
