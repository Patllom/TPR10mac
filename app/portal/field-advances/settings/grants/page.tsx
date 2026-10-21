import GrantScreen from '../GrantScreen';
export const dynamic = 'force-dynamic';
export default function GrantsPage({ searchParams }: { searchParams: Promise<Record<string, string | string[] | undefined>> }) {
  return <GrantScreen searchParams={searchParams} screen="grants" />;
}
