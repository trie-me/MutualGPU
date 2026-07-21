export function createScalarPayload(entries) {
  const scalars = {};
  for (const [key, value] of entries) {
    if (key === 'compute' || key === 'memory' || typeof value !== 'string' || value === '') continue;
    scalars[key] = value;
  }
  return scalars;
}
