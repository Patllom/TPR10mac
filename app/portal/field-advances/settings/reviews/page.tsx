import SettingsPage from '../page';
export const dynamic = 'force-dynamic';
export default function ReviewsPage({ searchParams }: { searchParams: Promise<Record<string, string | string[] | undefined>> }) {
  return <SettingsPage searchParams={searchParams.then(p => ({ ...p, view: 'reviews' }))} />;
}
