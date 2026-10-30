import type { ZodType } from 'zod';

export type FieldErrors = Record<string, string>;

export function validate<T>(schema: ZodType<T>, data: unknown): { success: true; data: T } | { success: false; errors: FieldErrors } {
  const result = schema.safeParse(data);
  if (result.success) return { success: true, data: result.data };
  const errors: FieldErrors = {};
  for (const issue of result.error.issues) {
    // Zod 4 paths are PropertyKey[]; Array.join throws on a symbol, so stringify each segment.
    const key = issue.path.map(String).join('.');
    if (!errors[key]) errors[key] = issue.message;
  }
  return { success: false, errors };
}
