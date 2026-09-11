// Tracks whether the backend is reachable. Flipped by the API client on
// every request: success resets the failure counter, two consecutive
// network/timeout errors flip the banner on. Anything else (4xx/5xx) is
// considered "reached the backend" and doesn't count.

const THRESHOLD = 2;

class ConnectionStore {
  offline = $state(false);
  private failures = 0;

  reportSuccess() {
    this.failures = 0;
    if (this.offline) this.offline = false;
  }

  reportFailure() {
    this.failures += 1;
    if (this.failures >= THRESHOLD && !this.offline) this.offline = true;
  }
}

export const connectionStore = new ConnectionStore();
