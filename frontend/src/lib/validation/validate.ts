import { type ZodSchema, type ZodError } from 'zod';

export type FieldErrors = Record<string, string>;

export function validate<T>(schema: ZodSchema<T>, data: unknown): { success: true; data: T } | { success: false; errors: FieldErrors } {
  const result = schema.safeParse(data);
  if (result.success) return { success: true, data: result.data };
  const errors: FieldErrors = {};
  for (const issue of result.error.issues) {
    const key = issue.path.join('.');
    if (!errors[key]) errors[key] = issue.message;
  }
  return { success: false, errors };
}
