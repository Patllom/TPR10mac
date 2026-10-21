import GrantScreen from '../GrantScreen';
export const dynamic = 'force-dynamic';
export default function HistoryPage({ searchParams }: { searchParams: Promise<Record<string, string | string[] | undefined>> }) {
  return <GrantScreen searchParams={searchParams} screen="history" />;
}
