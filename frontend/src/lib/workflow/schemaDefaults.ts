// Walk a JSON Schema's `properties` and collect declared `default` values,
// recursing into nested object schemas. Used to seed a runtime-input form
// (the manual Run dialog and the schedule editor) so the user starts from
// the workflow's declared defaults instead of a blank object.
export function buildSchemaDefaults(
  schema: Record<string, unknown> | null | undefined,
): Record<string, unknown> {
  if (!schema || typeof schema !== 'object') return {};
  const defaults: Record<string, unknown> = {};
  const properties = (schema.properties as Record<string, Record<string, unknown>>) || {};
  for (const [key, fieldSchema] of Object.entries(properties)) {
    if (!fieldSchema || typeof fieldSchema !== 'object') continue;
    if ('default' in fieldSchema) {
      defaults[key] = fieldSchema.default;
      continue;
    }
    if (fieldSchema.type === 'object') {
      defaults[key] = buildSchemaDefaults(fieldSchema);
    }
  }
  return defaults;
}
