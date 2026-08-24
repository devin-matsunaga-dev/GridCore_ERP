import {
  Activity,
  Building2,
  ChartColumn,
  ClipboardList,
  CircleDollarSign,
  CreditCard,
  FileText,
  House,
  LayoutDashboard,
  Package,
  Share2,
  ShoppingCart,
  Users,
  UsersRound,
  Zap,
  type LucideIcon,
} from 'lucide-react';

export type NavItem = {
  label: string;
  to: string;
  icon: LucideIcon;
};

export type NavSection = {
  /** `null` for the ungrouped item at the top of the sidebar (Home). */
  title: string | null;
  items: NavItem[];
};

/**
 * The sidebar, exactly as docs/Design.png shows it: an ungrouped Home, then Operations,
 * Enterprise and Reports under muted section labels. Routes land on placeholder pages until the
 * work package that owns each area fills them in.
 */
export const navigation: NavSection[] = [
  {
    title: null,
    items: [{ label: 'Home', to: '/', icon: House }],
  },
  {
    title: 'Operations',
    items: [
      { label: 'Work Management', to: '/work-orders', icon: ClipboardList },
      { label: 'Outage Management', to: '/outages', icon: Zap },
      { label: 'Field Operations', to: '/field-operations', icon: Users },
      { label: 'Assets', to: '/assets', icon: Building2 },
      { label: 'Network', to: '/network', icon: Share2 },
      { label: 'Monitoring', to: '/monitoring', icon: Activity },
    ],
  },
  {
    title: 'Enterprise',
    items: [
      { label: 'Finance', to: '/finance', icon: CircleDollarSign },
      { label: 'Procurement', to: '/procurement', icon: ShoppingCart },
      { label: 'Inventory', to: '/inventory', icon: Package },
      { label: 'Billing & Payments', to: '/billing', icon: CreditCard },
      { label: 'People', to: '/people', icon: UsersRound },
      { label: 'Customers', to: '/customers', icon: Users },
    ],
  },
  {
    title: 'Reports',
    items: [
      { label: 'Dashboards', to: '/dashboards', icon: LayoutDashboard },
      { label: 'Reports', to: '/reports', icon: FileText },
      { label: 'Analytics', to: '/analytics', icon: ChartColumn },
    ],
  },
];

/** Flattened, for route generation and tests. */
export const navigationItems: NavItem[] = navigation.flatMap((section) => section.items);
