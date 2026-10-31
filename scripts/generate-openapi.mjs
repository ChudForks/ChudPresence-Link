import fs from 'node:fs/promises';

const ref = name => ({ $ref: `#/components/schemas/${name}` });
const string = (extra = {}) => ({ type: 'string', ...extra });
const integer = (extra = {}) => ({ type: 'integer', format: 'int64', minimum: 0, ...extra });
const nullable = schema => ({ anyOf: [schema, { type: 'null' }] });
const object = (properties, required = [], extra = {}) => ({ type: 'object', properties, required, ...extra });
const map = schema => ({ type: 'object', additionalProperties: schema });
const servicePattern = '^[a-z][a-z0-9]*(?:-[a-z0-9]+)*$';
const services = schema => ({ ...map(schema), maxProperties: 256, propertyNames: { pattern: servicePattern, maxLength: 64 } });
const display = string({ enum: ['app', 'creator', 'title'] });
const settingField = string({ enum: ['display', 'enabled', 'showWhilePaused', 'artwork', 'playbackProgress', 'actionButtons'] });
const settingFields = { type: 'array', minItems: 1, maxItems: 6, uniqueItems: true, items: settingField };
const values = { display, enabled: { type: 'boolean', description: 'Enable this service; separate from enabling its settings sync.' },
  showWhilePaused: { type: 'boolean', description: 'Keep activity visible while paused.' },
  artwork: { type: 'boolean', description: 'Show cover art or a video thumbnail.' },
  playbackProgress: { type: 'boolean', description: 'Show media elapsed and remaining playback time. When false, Discord falls back to its green app/activity elapsed timer on both the browser extension and mobile app; that timer may continue while paused if the activity stays visible.' },
  actionButtons: { type: 'boolean', description: 'Show watch, play, or channel action buttons.' } };
const inputValues = Object.fromEntries(Object.entries(values).map(([key, value]) => [key, nullable(value)]));
const url = string({ format: 'uri', pattern: '^https://', maxLength: 256 });
const text = string({ description: 'At most 128 UTF-8 bytes; no control characters.' });
const schemas = {
  Error: object({ error: string(), message: string() }, ['error', 'message']),
  Info: object({ name: string({ const: 'ChudPresence Link' }), version: string(), protocolVersion: { type: 'integer', const: 1 },
    serverId: string(), capabilities: { type: 'array', items: string() }, displayChoices: { type: 'array', items: display }, settingFields,
    presenceLeaseSeconds: { type: 'integer', const: 30 } }, ['name', 'version', 'protocolVersion', 'serverId', 'capabilities', 'displayChoices', 'settingFields', 'presenceLeaseSeconds']),
  PairRequest: object({ code: string({ pattern: '^\\d{8}$' }), name: string({ minLength: 1, maxLength: 80 }),
    kind: string({ enum: ['browser', 'mobile'] }) }, ['code', 'name', 'kind']),
  PairResponse: object({ serverId: string(), deviceId: string(), token: string({ pattern: '^[a-f0-9]{64}$' }),
    protocolVersion: { type: 'integer', const: 1 } }, ['serverId', 'deviceId', 'token', 'protocolVersion']),
  ServiceValues: object(inputValues, [], { additionalProperties: false }),
  ServicePreference: object({ ...values, version: integer({ minimum: 1 }) }, ['version'], {
    description: 'Only selected settings are present. An omitted setting stays local to each client.', additionalProperties: false }),
  PreferenceChange: object({ ...inputValues, baseVersion: integer() }, ['baseVersion'], {
    description: 'Supply at least one selected setting. Omitted or null values are unchanged. The version applies to the whole service.',
    minProperties: 2, additionalProperties: false }),
  SettingsSnapshot: object({ revision: integer(), services: services(ref('ServicePreference')) }, ['revision', 'services']),
  SettingsRequest: object({ changes: { ...services(ref('PreferenceChange')), maxProperties: 128 } }, ['changes']),
  SettingsResult: object({ revision: integer(), services: services(ref('ServicePreference')), conflicts: { type: 'array', items: string() } }, ['revision', 'services', 'conflicts']),
  ClientService: object({ ...inputValues, id: string({ pattern: servicePattern, maxLength: 64 }), name: string({ minLength: 1, maxLength: 80 }),
    displayChoices: { type: 'array', maxItems: 3, uniqueItems: true, items: display,
      description: 'Defaults to all three choices when display is present; otherwise empty. Without display, omit or send an empty array.' } }, ['id', 'name'], {
    description: 'Report at least one current supported setting. Omit unsupported settings; explicit false advertises boolean support. A display needs a supported current value.', additionalProperties: false }),
  ServicesRequest: object({ services: { type: 'array', maxItems: 128, items: ref('ClientService') } }, ['services']),
  ServiceSyncRequest: object({ enabled: { type: 'boolean' }, baseVersion: integer(), sourceDeviceId: nullable(string()), initialDisplay: nullable(display),
    fields: { ...settingFields, default: ['display'], description: 'Explicit settings to share. For all currently supported settings, send supportedFields from discovery.' },
    initialSettings: nullable(ref('ServiceValues')) }, ['enabled', 'baseVersion'], { additionalProperties: false,
    description: 'Enabling requires exactly one sourceDeviceId, initialDisplay, or initialSettings and values for every selected field. Disabling accepts only enabled and baseVersion.' }),
  ServiceParticipant: object({ ...values, deviceId: string(), name: string(), kind: string({ enum: ['browser', 'mobile'] }),
    reportedAt: string({ format: 'date-time' }) }, ['deviceId', 'name', 'kind', 'reportedAt']),
  SharedService: object({ id: string(), name: string(), shared: { type: 'boolean' }, syncEnabled: { type: 'boolean' }, syncVersion: integer(),
    displayChoices: { type: 'array', items: display }, participants: { type: 'array', items: ref('ServiceParticipant') },
    supportedFields: { type: 'array', uniqueItems: true, items: settingField }, syncFields: settingFields,
    syncRequested: { type: 'boolean', description: 'Saved opt-in; true with syncEnabled false means sync is paused for incompatibility or missing overlap.' } },
    ['id', 'name', 'shared', 'syncEnabled', 'syncVersion', 'displayChoices', 'participants', 'supportedFields', 'syncFields', 'syncRequested']),
  ServicesSnapshot: object({ revision: integer(), services: { type: 'array', items: ref('SharedService') } }, ['revision', 'services']),
  LeaseRequest: object({ leaseId: nullable(string()) }),
  LeaseView: object({ leaseId: string(), expiresAt: string({ format: 'date-time' }), heartbeatSeconds: { type: 'integer', const: 10 },
    fallbackGraceSeconds: { type: 'integer', const: 5 } }, ['leaseId', 'expiresAt', 'heartbeatSeconds', 'fallbackGraceSeconds']),
  PresenceRequest: object({ leaseId: string(), sequence: integer(), intent: nullable(ref('PresenceIntent')) }, ['leaseId', 'sequence', 'intent']),
  PresenceIntent: object({ name: { ...text, minLength: 1 }, details: { ...text, minLength: 1 }, type: string({ enum: ['playing', 'streaming', 'listening', 'watching', 'competing'], default: 'watching' }),
    state: nullable(text), statusDisplayType: string({ enum: ['name', 'state', 'details'], default: 'name' }),
    detailsUrl: nullable(url), stateUrl: nullable(url), timestamps: nullable(ref('PresenceTimestamps')), assets: nullable(ref('PresenceAssets')),
    buttons: nullable({ type: 'array', maxItems: 2, items: ref('PresenceButton') }) }, ['name', 'details']),
  PresenceTimestamps: object({ start: nullable(integer({ maximum: 8640000000000, description: 'Unix seconds, not milliseconds.' })),
    end: nullable(integer({ maximum: 8640000000000, description: 'Unix seconds; must not precede start.' })) }),
  PresenceAssets: object({ largeImage: nullable(url), largeText: nullable(text), largeUrl: nullable(url), smallImage: nullable(url), smallText: nullable(text) }),
  PresenceButton: object({ label: string({ minLength: 1, description: 'At most 32 UTF-8 bytes; no control characters.' }), url }, ['label', 'url']),
  PresenceStatus: object({ discordConnected: { type: 'boolean' }, message: string(), deviceId: nullable(string()),
    expiresAt: nullable(string({ format: 'date-time' })), details: nullable(string()), published: { type: 'boolean' } },
    ['discordConnected', 'message', 'deviceId', 'expiresAt', 'details', 'published']),
};
const response = (description, schema) => ({ description, ...(schema ? { content: { 'application/json': { schema: ref(schema) } } } : {}) });
const operation = (operationId, description, { input, output, status = '200', public: isPublic = false, parameters } = {}) => ({
  operationId, description, ...(isPublic ? { security: [] } : {}), ...(parameters ? { parameters } : {}),
  ...(input ? { requestBody: { required: true, content: { 'application/json': { schema: ref(input) } } } } : {}),
  responses: { [status]: response(status === '204' ? 'Completed; no response body.' : 'Success.', output),
    default: response('API error. See docs/API.md for status codes, ownership rules, and Retry-After.', 'Error') },
});
const specification = {
  openapi: '3.1.0',
  info: { title: 'ChudPresence Link', version: '1.3.0', description: 'Client-reported browser/phone service discovery, per-service selective settings sync, change events, and loopback desktop presence. See API.md and CONNECTING.md for integration.' },
  servers: [{ url: 'http://127.0.0.1:17879' }], security: [{ pairingToken: [] }],
  paths: {
    '/v1/info': { get: operation('getInfo', 'Discover protocol capabilities and stable server identity.', { output: 'Info', public: true }) },
    '/v1/pair': { post: operation('pairDevice', 'Redeem a native-UI pairing code. Browser Origin is required and bound to the token; mobile must omit Origin. Five attempts per IP per minute.', { input: 'PairRequest', output: 'PairResponse', public: true }) },
    '/v1/device': { delete: operation('unpairSelf', 'Revoke the calling token and clear its presence.', { status: '204' }) },
    '/v1/services': {
      get: operation('discoverServices', 'Find the caller\'s installed services shared by a browser and phone, common settings, selected fields, reported source values, and opt-in state.', { output: 'ServicesSnapshot' }),
      put: operation('reportServices', 'Replace the caller\'s full installed service inventory. Empty removes all. Reporting never enables sync or changes shared preferences.', { input: 'ServicesRequest', output: 'ServicesSnapshot' }),
    },
    '/v1/services/{serviceId}/sync': { put: operation('configureServiceSync', 'Enable, reconfigure, or disable synchronization for a reported service. Select fields from supportedFields and exactly one sourceDeviceId, initialDisplay, or initialSettings. Omitting fields selects display only. baseVersion is syncVersion from discovery.', {
      input: 'ServiceSyncRequest', output: 'ServicesSnapshot', parameters: [{ name: 'serviceId', in: 'path', required: true, schema: string({ pattern: servicePattern, maxLength: 64 }) }],
    }) },
    '/v1/settings': {
      get: operation('getSettings', 'Get only selected settings for the caller\'s reported services with active sync. Unselected fields are omitted.', { output: 'SettingsSnapshot' }),
      patch: operation('changeSettings', 'Partially update selected settings with a per-service compare-and-swap version. Omitted fields remain unchanged. Unselected fields reject the batch with field_not_synced; stale conflicts return 200 with the filtered snapshot and conflicts array.', { input: 'SettingsRequest', output: 'SettingsResult' }),
    },
    '/v1/events': { get: {
      operationId: 'watchChanges',
      description: 'Authenticated Server-Sent Events stream. Immediately emits the current revision and serverId, then coalesced revision hints after durable changes; fetch services and settings after each hint. Sends an idle comment every 120 seconds. Revocation emits a revoked event and closes the stream. Opening another stream for the same device closes its previous stream.',
      responses: {
        '200': { description: 'Streaming revision and revocation events.', content: { 'text/event-stream': {
          schema: { type: 'string' }, example: 'id: 7\nevent: revision\ndata: {"serverId":"server-id","revision":7}\n\n',
        } } },
        default: response('API error. See docs/API.md for status codes and Retry-After.', 'Error'),
      },
    } },
    '/v1/status': { get: operation('getStatus', 'Inspect desktop publishing. May contain the current browser title.', { output: 'PresenceStatus' }) },
    '/v1/presence/lease': { post: operation('acquirePresence', 'Loopback browser only. Reserve a 30-second lease after connecting to Discord. Clear headless publishing before sending desktop intent. Send existing leaseId to renew reservation.', { input: 'LeaseRequest', output: 'LeaseView' }) },
    '/v1/presence': { put: operation('updatePresence', 'Accept latest presence and renew ownership. Increment sequence and heartbeat every 10 seconds. Null intent clears presence while retaining ownership. Acceptance is asynchronous.', { input: 'PresenceRequest', output: 'LeaseView' }) },
    '/v1/presence/lease/{leaseId}': { delete: operation('releasePresence', 'Clear activity and release ownership. On network failure wait 35 seconds after the last attempted request finishes or times out before headless fallback.', {
      status: '204', parameters: [{ name: 'leaseId', in: 'path', required: true, schema: string() }],
    }) },
  },
  components: { securitySchemes: { pairingToken: { type: 'http', scheme: 'bearer', bearerFormat: 'opaque 64-character token' } }, schemas },
};
const target = new URL('../docs/openapi.json', import.meta.url);
const content = JSON.stringify(specification, null, 2) + '\n';
if (process.argv.includes('--check')) {
  if (await fs.readFile(target, 'utf8') !== content) throw new Error('docs/openapi.json is stale. Run node scripts/generate-openapi.mjs.');
} else await fs.writeFile(target, content);
console.log('OpenAPI definition is current.');
