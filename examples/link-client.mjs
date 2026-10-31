// Reference only. This file is not installed into either existing ChudPresence app.
// Browser fetch supplies Origin itself. Pass origin only for Node-based protocol tools.
export class LinkClient {
  constructor(baseUrl = 'http://127.0.0.1:17879', { token = '', serverId = '', origin = '', timeoutMs = 5000 } = {}) {
    const url = new URL(baseUrl);
    if (url.protocol !== 'http:' || url.username || url.password || url.pathname !== '/' || url.search || url.hash) {
      throw new TypeError('Use the HTTP origin of ChudPresence Link.');
    }
    const host = url.hostname.replace(/^\[|\]$/g, '');
    const bytes = host.split('.').map(Number);
    const privateIpv4 = bytes.length === 4 && bytes.every(n => Number.isInteger(n) && n >= 0 && n <= 255) &&
      (bytes[0] === 127 || bytes[0] === 10 || bytes[0] === 192 && bytes[1] === 168 ||
       bytes[0] === 172 && bytes[1] >= 16 && bytes[1] <= 31 || bytes[0] === 169 && bytes[1] === 254);
    if (host !== 'localhost' && !privateIpv4 && host !== '::1' && !/^(?:f[cd][0-9a-f]{2}|fe[89ab][0-9a-f]):/i.test(host)) {
      throw new TypeError('Use a loopback or private LAN address.');
    }
    if (token && !serverId) throw new TypeError('Restore the pinned serverId alongside the pairing token.');
    this.baseUrl = url.origin;
    this.token = token;
    this.serverId = serverId;
    this.origin = origin;
    this.timeoutMs = timeoutMs;
    this.lease = null;
    this.sequence = 0;
  }

  async request(path, { method = 'GET', body, authenticated = true } = {}) {
    // Every fetch may establish a new connection after a restart or LAN address change.
    // Verify the pinned identity before any method sends the pairing token.
    if (authenticated) await this.discover();
    const headers = {};
    if (this.origin) headers.Origin = this.origin;
    if (authenticated) headers.Authorization = `Bearer ${this.token}`;
    if (body !== undefined) headers['Content-Type'] = 'application/json';
    const response = await fetch(this.baseUrl + path, {
      method, headers, body: body === undefined ? undefined : JSON.stringify(body),
      signal: AbortSignal.timeout(this.timeoutMs), cache: 'no-store', credentials: 'omit', redirect: 'error',
    });
    const raw = response.status === 204 ? '' : await response.text();
    let payload = null;
    try { if (raw) payload = JSON.parse(raw); }
    catch { if (response.ok) throw new Error('Link returned an invalid JSON response.'); }
    if (!response.ok) {
      const error = new Error(payload?.message || `Link returned HTTP ${response.status}.`);
      error.status = response.status;
      error.code = payload?.error;
      error.retryAfter = response.headers.get('Retry-After');
      throw error;
    }
    return payload;
  }

  async discover() {
    const info = await this.request('/v1/info', { authenticated: false });
    if (!info || info.protocolVersion !== 1 || info.name !== 'ChudPresence Link' || typeof info.serverId !== 'string' || !info.serverId) {
      throw new Error('Unsupported Link server.');
    }
    if (this.serverId && info.serverId !== this.serverId) throw new Error('Link server identity changed. Pair again.');
    return info;
  }

  async pair(code, name, kind) {
    const info = await this.discover();
    const result = await this.request('/v1/pair', { method: 'POST', authenticated: false, body: { code, name, kind } });
    if (result.serverId !== info.serverId) throw new Error('Link server changed during pairing.');
    this.serverId = result.serverId;
    this.token = result.token;
    return result; // Persist privately in the host client's credential storage.
  }

  settings() { return this.request('/v1/settings'); }

  services() { return this.request('/v1/services'); }

  async reportServices(services) {
    return this.request('/v1/services', { method: 'PUT', body: { services } });
  }

  async setServiceSync(serviceId, { enabled, baseVersion, sourceDeviceId, initialDisplay, fields, initialSettings } = {}) {
    return this.request('/v1/services/' + encodeURIComponent(serviceId) + '/sync', {
      method: 'PUT', body: { enabled, baseVersion, sourceDeviceId, initialDisplay, fields, initialSettings },
    });
  }

  async changeSettings(changes) {
    return this.request('/v1/settings', { method: 'PATCH', body: { changes } });
  }

  async acquire() {
    const previous = this.lease?.leaseId;
    this.lease = await this.request('/v1/presence/lease', { method: 'POST', body: this.lease ? { leaseId: this.lease.leaseId } : {} });
    if (previous !== this.lease.leaseId) this.sequence = 0;
    return this.lease;
  }

  async publish(intent) {
    if (!this.lease) throw new Error('Acquire a lease and clear the headless session first.');
    this.lease = await this.request('/v1/presence', { method: 'PUT', body: {
      leaseId: this.lease.leaseId, sequence: this.sequence++, intent,
    } });
    return this.lease;
  }

  status() { return this.request('/v1/status'); }

  async release() {
    if (!this.lease) return;
    await this.request('/v1/presence/lease/' + encodeURIComponent(this.lease.leaseId), { method: 'DELETE' });
    this.lease = null;
  }

  async unpair() {
    await this.request('/v1/device', { method: 'DELETE' });
    this.token = ''; this.lease = null;
  }
}

// Map only these presentation values. Do not pass credentials or all local storage.
export const browserDisplayToLink = Object.freeze({ app: 'app', artist: 'creator', track: 'title' });
export const mobileDisplayToLink = Object.freeze({ app_name: 'app', artist: 'creator', track: 'title' });
export const settingFields = Object.freeze(['display', 'enabled', 'showWhilePaused', 'artwork', 'playbackProgress', 'actionButtons']);

// After pairing, report ONLY the services installed/supported in this client:
// await client.reportServices([{ id: 'your-service-id', name: 'Your service', display: 'creator',
//   enabled: true, showWhilePaused: false, artwork: true, playbackProgress: true, actionButtons: true }]);
// Omit settings the client cannot read and apply; explicit false still advertises support.
// Offer items from await client.services() where shared is true; keep sync off until selected.
// await client.setServiceSync(service.id, { enabled: true, baseVersion: service.syncVersion,
//   sourceDeviceId: chosenParticipant.deviceId, fields: service.supportedFields }); // all currently supported
// Use fields: ['enabled', 'artwork'] for a subset. Omitted fields keeps legacy display-only sync.
// PATCH only selected fields: { [service.id]: { baseVersion: saved.version, artwork: false } }.
// Then fetch settings and PATCH explicit local edits using their per-service versions.
// Inspect result.conflicts and apply the authoritative response as docs/API.md describes.
// Callers must serialize publish/release operations, preserve sequence across heartbeats,
// and implement the lease-expiry grace period before restoring headless publishing.
