export type Debounced<TArgs extends unknown[]> = ((...args: TArgs) => void) & {
  cancel: () => void;
  flush: () => void;
};

export function debounce<TArgs extends unknown[]>(
  fn: (...args: TArgs) => void,
  delayMs = 250,
): Debounced<TArgs> {
  let timer: ReturnType<typeof setTimeout> | undefined;
  let lastArgs: TArgs | undefined;

  const wrapped = ((...args: TArgs) => {
    lastArgs = args;
    if (timer) clearTimeout(timer);
    timer = setTimeout(() => {
      timer = undefined;
      if (lastArgs) fn(...lastArgs);
    }, delayMs);
  }) as Debounced<TArgs>;

  wrapped.cancel = () => {
    if (timer) clearTimeout(timer);
    timer = undefined;
    lastArgs = undefined;
  };
  wrapped.flush = () => {
    if (timer) {
      clearTimeout(timer);
      timer = undefined;
      if (lastArgs) fn(...lastArgs);
    }
  };

  return wrapped;
}
