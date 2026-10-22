import SettingsPage from '../../page';
export const dynamic = 'force-dynamic';
export default async function ProjectPage({ searchParams, params }: { searchParams: Promise<Record<string, string | string[] | undefined>>; params: Promise<{ projectId: string }> }) {
  const { projectId } = await params;
  return <SettingsPage searchParams={searchParams.then(p => ({ ...p, projectId, view: 'project' }))} />;
}
