export type ThumbnailWarmupCandidate = {
  id: string;
};

export function collectLimitedThumbnailWarmupPaths(
  images: ThumbnailWarmupCandidate[],
  acceptsPath: (imagePath: string) => boolean,
  limit: number
) {
  const maxPaths = Math.max(0, Math.trunc(limit));
  if (maxPaths <= 0) return [];

  const paths: string[] = [];
  for (const image of images) {
    if (!acceptsPath(image.id)) continue;
    paths.push(image.id);
    if (paths.length >= maxPaths) break;
  }

  return paths;
}
