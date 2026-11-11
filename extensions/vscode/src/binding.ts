import * as path from 'node:path';

export interface ProjectMetadata {
  id: string;
  sourceRoot?: string;
}

export function projectForRoot<T extends ProjectMetadata>(root: string, projects: T[], binding?: string): T | undefined {
  if (binding) {
    return projects.find((project) => project.id.toLowerCase() === binding.toLowerCase());
  }
  const normalize = (value: string): string => process.platform === 'win32' ? path.resolve(value).toLowerCase() : path.resolve(value);
  const matches = projects.filter((project) => project.sourceRoot && path.isAbsolute(project.sourceRoot)
    && normalize(project.sourceRoot) === normalize(root));
  return matches.length === 1 ? matches[0] : undefined;
}

export function isProjectId(value: unknown): value is string {
  return typeof value === 'string' && /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(value);
}