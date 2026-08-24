import {
  Boxes,
  Building2,
  ClipboardList,
  DollarSign,
  PackagePlus,
  ShoppingCart,
  Users,
  Zap,
  type LucideIcon,
} from 'lucide-react';
import type { StatusTone } from '@/components/ui/status';

/**
 * Placeholder figures so the shell renders the reference dashboard (docs/Design.png) exactly.
 * WP-4.3 owns the real dashboards and replaces this file with queries; nothing here is fetched,
 * persisted or used by any other feature.
 */

export type Kpi = {
  label: string;
  value: string;
  icon: LucideIcon;
  /** Signed fraction: -0.064 renders as a 6.4% fall. */
  change: number;
  comparedTo: string;
  /** Whether a fall is good news — operating cost down is green, assets down is not. */
  fallIsGood: boolean;
  /** Twelve points behind the headline number, drawn as a sparkline. */
  trend: number[];
};

export const kpis: Kpi[] = [
  {
    label: 'Total Operating Cost (MTD)',
    value: '$24.8M',
    icon: DollarSign,
    change: -0.064,
    comparedTo: 'Apr 2025',
    fallIsGood: true,
    trend: [31, 29, 30, 27, 28, 25, 26, 24, 25, 23, 24, 22],
  },
  {
    label: 'Procurement Spend (YTD)',
    value: '$18.6M',
    icon: ShoppingCart,
    change: -0.087,
    comparedTo: 'May 2024',
    fallIsGood: true,
    trend: [12, 14, 13, 16, 15, 18, 17, 19, 18, 21, 20, 23],
  },
  {
    label: 'Inventory Value',
    value: '$7.3M',
    icon: Boxes,
    change: 0.032,
    comparedTo: 'Apr 2025',
    fallIsGood: false,
    trend: [5.9, 6.1, 6.0, 6.4, 6.3, 6.6, 6.5, 6.8, 7.0, 6.9, 7.2, 7.3],
  },
  {
    label: 'Active Employees',
    value: '1,274',
    icon: Users,
    change: 0.018,
    comparedTo: 'Apr 2025',
    fallIsGood: false,
    trend: [1180, 1195, 1190, 1210, 1205, 1225, 1230, 1245, 1240, 1258, 1266, 1274],
  },
  {
    label: 'Total Assets',
    value: '12,842',
    icon: Building2,
    change: 0.021,
    comparedTo: 'Apr 2025',
    fallIsGood: false,
    trend: [12100, 12180, 12150, 12320, 12290, 12440, 12480, 12560, 12610, 12700, 12780, 12842],
  },
];

export type GridStatus = { label: string; tone: StatusTone; value: string };

export const gridStatus: GridStatus[] = [
  { label: 'Online', tone: 'success', value: '98.6%' },
  { label: 'Warning', tone: 'warning', value: '12' },
  { label: 'Outage', tone: 'danger', value: '3' },
  { label: 'Maintenance', tone: 'neutral', value: '5' },
];

export const territories = ['Rota Island', 'Northern District', 'Harbour District'];

export type WorkOrderSlice = { status: string; count: number; color: string };

export const workOrdersByStatus: WorkOrderSlice[] = [
  { status: 'Completed', count: 645, color: 'var(--success)' },
  { status: 'In Progress', count: 352, color: 'var(--info)' },
  { status: 'Scheduled', count: 211, color: 'var(--warning)' },
  { status: 'On Hold', count: 78, color: 'var(--chart-5)' },
];

export type Alert = {
  id: string;
  title: string;
  detail: string;
  tone: StatusTone;
  /** Minutes ago, resolved against the render clock so the card never shows a stale timestamp. */
  minutesAgo: number;
};

export const alerts: Alert[] = [
  { id: 'a1', title: 'Low Inventory: Transformer Oil', detail: 'Only 120 gallons remaining', tone: 'warning', minutesAgo: 10 },
  { id: 'a2', title: 'Work Order Overdue', detail: 'WO-84291 is past due', tone: 'warning', minutesAgo: 60 },
  { id: 'a3', title: 'Asset Maintenance Due', detail: '33 assets require maintenance', tone: 'danger', minutesAgo: 180 },
  { id: 'a4', title: 'Backup Completed', detail: 'System backup completed successfully', tone: 'success', minutesAgo: 300 },
];

export type WorkOrderRow = {
  id: string;
  type: string;
  description: string;
  status: string;
  priority: string;
  createdAt: string;
};

const feedSeeds: [string, string, string, string, string][] = [
  ['Repair', 'Pole Replacement – 3rd Ave', 'In Progress', 'High', '9:15 AM'],
  ['Inspection', 'Transformer Inspection – TX-12', 'Scheduled', 'Medium', '8:40 AM'],
  ['Repair', 'Underground Line Repair', 'In Progress', 'High', '8:02 AM'],
  ['Maintenance', 'Preventive Maintenance', 'Completed', 'Low', '7:30 AM'],
  ['Inspection', 'Substation Inspection – SS-04', 'Scheduled', 'Medium', '7:05 AM'],
  ['Outage', 'Streetlight Outage – Main St', 'On Hold', 'Low', '6:48 AM'],
  ['Repair', 'Feeder Fault – Harbour Rd', 'In Progress', 'High', '6:20 AM'],
  ['Maintenance', 'Recloser Service – RC-19', 'Completed', 'Low', '5:55 AM'],
  ['Inspection', 'Pole Audit – District 4', 'Scheduled', 'Medium', '5:30 AM'],
  ['Repair', 'Meter Replacement – 118 Elm', 'Completed', 'Low', '5:02 AM'],
  ['Outage', 'Planned Outage – Circuit 7', 'Scheduled', 'Medium', '4:40 AM'],
  ['Repair', 'Cross-Arm Replacement', 'In Progress', 'High', '4:15 AM'],
  ['Maintenance', 'Vegetation Management', 'On Hold', 'Low', '3:50 AM'],
  ['Inspection', 'Thermal Scan – SS-02', 'Completed', 'Low', '3:22 AM'],
  ['Repair', 'Service Drop Repair', 'In Progress', 'Medium', '2:58 AM'],
  ['Outage', 'Streetlight Outage – Bay Ave', 'Scheduled', 'Low', '2:30 AM'],
  ['Maintenance', 'Breaker Test – SS-01', 'Completed', 'Low', '2:05 AM'],
  ['Inspection', 'Line Patrol – Feeder 3', 'Scheduled', 'Medium', '1:40 AM'],
  ['Repair', 'Guy Wire Tension – Pole 4412', 'On Hold', 'Low', '1:12 AM'],
  ['Maintenance', 'Regulator Service – RG-08', 'Completed', 'Low', '12:45 AM'],
];

export const workOrderFeed: WorkOrderRow[] = feedSeeds.map(
  ([type, description, status, priority, createdAt], index) => ({
    id: `WO-${84321 - index}`,
    type,
    description,
    status,
    priority,
    createdAt,
  }),
);

export type SpendPoint = {
  month: string;
  /** Spend in that month — the bars. */
  monthly: number;
  /** Running total to date — the green line; `null` once the year runs past today. */
  ytd: number | null;
  /** Running budget to date — the dashed line. */
  budget: number;
};

const monthlySpend = [1.1, 1.2, 1.25, 1.35, 1.4, 1.5, 1.55, 1.65, 1.7, 1.8, 1.9, 2.2];
const monthlyBudget = 21 / 12;

export const procurementSpend: SpendPoint[] = [
  'Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec',
].map((month, index) => ({
  month,
  monthly: monthlySpend[index]! * 1_000_000,
  ytd: monthlySpend.slice(0, index + 1).reduce((sum, value) => sum + value, 0) * 1_000_000,
  budget: monthlyBudget * (index + 1) * 1_000_000,
}));

export const procurementTotals = {
  ytdSpend: 18_600_000,
  budget: 21_000_000,
  /** Negative = under budget, which for spend is good news. */
  variance: -2_400_000,
  variancePercent: -0.114,
};

export type QuickAction = { label: string; description: string; icon: LucideIcon; to: string };

export const quickActions: QuickAction[] = [
  {
    label: 'Create Work Order',
    description: 'Log and assign a new work order',
    icon: ClipboardList,
    to: '/work-orders',
  },
  {
    label: 'Add Inventory',
    description: 'Receive or add inventory to stock',
    icon: PackagePlus,
    to: '/inventory',
  },
  {
    label: 'Report Outage',
    description: 'Report an outage or service issue',
    icon: Zap,
    to: '/outages',
  },
];

/** The dashboard's "data as of" stamp. Fixed so the demo reads the same every run. */
export const dataAsOf = new Date('2025-05-20T07:45:00');
