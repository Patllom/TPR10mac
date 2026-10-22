import SettingsPage from '../page';
import GrantScreen from '../GrantScreen';
export const dynamic = 'force-dynamic';
export default async function ReviewsPage({ searchParams }: { searchParams: Promise<Record<string, string | string[] | undefined>> }) {
  if ((await searchParams).domain === 'grants') return <GrantScreen searchParams={searchParams} screen="reviews" />;
  return <SettingsPage searchParams={searchParams.then(p => ({ ...p, view: 'reviews' }))} />;
}
