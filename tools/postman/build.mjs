// Builds the Postman collection and environment in docs/postman from the request table below.
//
//   node tools/postman/build.mjs
//
// Import both files into Postman (Import > the two .json files), pick the "Lateral CMS - local" environment, fill in the passwords
// (they are left empty on purpose) and press Send. A test (ApiDocsTests) fails if a route the service maps has no request here, or a
// request here has no route, so the collection cannot fall behind the API.
import { mkdirSync, writeFileSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const out = resolve(dirname(fileURLToPath(import.meta.url)), '..', '..', 'docs', 'postman');
const JSON_HEADER = { key: 'Content-Type', value: 'application/json' };
const SCRIPTED = { key: 'X-Requested-With', value: 'postman' };

// ---- the helpers that make requests ---------------------------------------------------------------------------------------
const auth = (who) => who === 'none' ? { type: 'noauth' } : {
  type: 'basic',
  basic: [{ key: 'username', value: `{{${who}User}}`, type: 'string' }, { key: 'password', value: `{{${who}Password}}`, type: 'string' }],
};

const url = (path, query = []) => ({
  raw: `{{baseUrl}}${path}${query.length ? '?' + query.map((q) => `${q[0]}=${q[1]}`).join('&') : ''}`,
  host: ['{{baseUrl}}'],
  path: path.split('/').filter(Boolean),
  query: query.map(([key, value]) => ({ key, value })),
});

const test = (lines) => [{ listen: 'test', script: { type: 'text/javascript', exec: lines } }];
const prerequest = (lines) => [{ listen: 'prerequest', script: { type: 'text/javascript', exec: lines } }];

/** One request. who: whose Basic credentials ('admin', 'user', 'cms', or 'none'). */
function req(name, method, path, { who, body, raw = 'json', query, headers = [], note, tests, pre, contentType } = {}) {
  const request = { method, header: [...headers], url: url(path, query), description: note };
  if (who) request.auth = auth(who);
  if (body !== undefined) {
    // A version is a number, not text, so its placeholder goes in without quotes.
    const text = typeof body === 'string' ? body : JSON.stringify(body, null, 2).replace(/"\{\{version\}\}"/g, '{{version}}');
    request.body = { mode: 'raw', raw: text, options: { raw: { language: raw } } };
    if (raw === 'json') request.header.push(JSON_HEADER);
    if (contentType) request.header.push({ key: 'Content-Type', value: contentType });
  }
  const item = { name, request };
  const events = [...(pre ? prerequest(pre) : []), ...(tests ? test(tests) : [])];
  if (events.length) item.event = events;
  return item;
}

const folder = (name, description, item, who) => ({ name, description, item, ...(who ? { auth: auth(who) } : {}) });

// A publish needs a version that only goes up and a time that is now: make both on each send.
const freshVersion = ["pm.variables.set('version', String(Date.now()));"];

// ---- the requests ---------------------------------------------------------------------------------------------------------
const collection = {
  info: {
    name: 'Lateral CMS',
    description: 'Every route of the Lateral CMS API, ready to send. Pick the "Lateral CMS - local" environment and fill in the passwords first.\n\n'
      + 'Folders are in the order you would use them: send events as the CMS, follow the batch, read the record, upload a file, download it, '
      + 'then the admin side. Requests save the ids they get back (batchId, entityId, assetId, uploadId) so the next one just works.\n\n'
      + 'The same API is described, and can be tried, at {{baseUrl}}/swagger.',
    schema: 'https://schema.getpostman.com/json/collection/v2.1.0/collection.json',
  },
  auth: auth('admin'),
  event: prerequest(freshVersion),
  item: [
    folder('1. CMS: events', 'The CMS service account sends events and follows its batches.', [
      req('Send a publish', 'POST', '/cms/events', {
        note: 'Publishes (creates or updates) a record. A 202 means the batch is stored durably, not yet applied. The X-Correlation-Id header is its batch id.',
        body: [{ type: 'publish', id: '{{eventId}}', version: '{{version}}', timestamp: '{{$isoTimestamp}}', payload: { title: 'Hello from Postman', note: 'change me' } }],
        tests: [
          "pm.test('accepted (202)', () => pm.response.to.have.status(202));",
          "const id = pm.response.headers.get('X-Correlation-Id'); if (id) pm.collectionVariables.set('batchId', id);",
        ],
      }),
      req('Send an unpublish', 'POST', '/cms/events', {
        note: 'Hides the record from users but keeps it (admins still see it). Needs a higher version than the last event for the same id; each send makes one.',
        body: [{ type: 'unPublish', id: '{{eventId}}', version: '{{version}}', timestamp: '{{$isoTimestamp}}', payload: { title: 'Hello from Postman' } }],
        tests: ["pm.test('accepted (202)', () => pm.response.to.have.status(202));", "const id = pm.response.headers.get('X-Correlation-Id'); if (id) pm.collectionVariables.set('batchId', id);"],
      }),
      req('Send a delete', 'POST', '/cms/events', {
        note: 'Removes the record and its content, leaving only a tombstone. A delete carries no version and no payload.',
        body: [{ type: 'delete', id: '{{eventId}}', timestamp: '{{$isoTimestamp}}' }],
        tests: ["pm.test('accepted (202)', () => pm.response.to.have.status(202));", "const id = pm.response.headers.get('X-Correlation-Id'); if (id) pm.collectionVariables.set('batchId', id);"],
      }),
      req('Send a mixed batch', 'POST', '/cms/events', {
        note: 'Several events at once. Order inside the batch does not matter; the end state is the same.',
        body: [
          { type: 'publish', id: 'postman-a', version: '{{version}}', timestamp: '{{$isoTimestamp}}', payload: { title: 'A' } },
          { type: 'publish', id: 'postman-b', version: '{{version}}', timestamp: '{{$isoTimestamp}}', payload: { title: 'B' } },
          { type: 'delete', id: 'postman-never-existed', timestamp: '{{$isoTimestamp}}' },
        ],
        tests: ["pm.test('accepted (202)', () => pm.response.to.have.status(202));", "const id = pm.response.headers.get('X-Correlation-Id'); if (id) pm.collectionVariables.set('batchId', id);"],
      }),
      req('Follow the batch', 'GET', '/cms/batches/{{batchId}}', {
        note: 'queued, completed, or failed, with each event\'s outcome (applied, ignored_stale, ...). Send again until it says completed.',
        tests: ["pm.test('found (200)', () => pm.response.to.have.status(200));"],
      }),
    ], 'cms'),

    folder('2. Records', 'Reading what the events built. Nothing here can change a record\'s content.', [
      req('List records', 'GET', '/api/entities', {
        note: 'A page of records in id order. Pass the nextCursor from one page as cursor to get the next. Admins also see unpublished and disabled records.',
        query: [['limit', '20']],
        tests: ["const items = (pm.response.json().items || []); if (items.length) pm.collectionVariables.set('entityId', items[0].id);"],
      }),
      req('Read one record', 'GET', '/api/entities/{{entityId}}', { note: 'One record. A hidden record answers 404, the same as one that does not exist.' }),
      req('Disable a record (admin)', 'POST', '/api/entities/{{entityId}}/disable', { note: 'Hides it from users, keeps it. A later publish does not undo it.' }),
      req('Enable a record (admin)', 'POST', '/api/entities/{{entityId}}/enable', { note: 'Reverses a disable.' }),
    ], 'admin'),

    folder('3. CMS: files', 'The CMS uploads files: one request for a small file, or a multipart upload for a big one.', [
      req('Upload a small file', 'PUT', '/cms/assets/{{assetKey}}', {
        note: 'The body is the file as is. Here it is a line of text; in Postman choose Body > binary to send a real picture. The file waits in quarantine until the scanner says clean.',
        body: 'Hello from Postman', raw: 'text', contentType: 'text/plain',
        headers: [],
        tests: ["pm.test('accepted (202)', () => pm.response.to.have.status(202));", "const id = pm.response.json().id; if (id) pm.collectionVariables.set('assetId', id);"],
      }),
      req('Start a multipart upload', 'POST', '/cms/assets/uploads', {
        note: 'For large files. Give the key, type and total size in bytes. The demo is 11 bytes, one part.',
        body: { key: 'multipart-demo.txt', contentType: 'text/plain', size: 11 },
        tests: ["pm.test('created (201)', () => pm.response.to.have.status(201));", "const id = pm.response.json().uploadId; if (id) pm.collectionVariables.set('uploadId', id);"],
      }),
      req('Upload a part', 'PUT', '/cms/assets/uploads/{{uploadId}}/parts/{{partNumber}}', {
        note: 'Send each part\'s bytes as the body. Parts may go in parallel, and sending the same part again is harmless. The demo sends part 1 (partNumber), which is exactly 11 bytes.',
        body: 'hello world', raw: 'text', contentType: 'application/octet-stream',
        tests: ["pm.test('part stored (200)', () => pm.response.to.have.status(200));"],
      }),
      req('Complete the upload', 'POST', '/cms/assets/uploads/{{uploadId}}/complete', {
        note: 'List the parts in order. The service joins them, checks the size, and queues the file for scanning.',
        body: { parts: [{ partNumber: 1 }] },
        tests: ["pm.test('accepted (202)', () => pm.response.to.have.status(202));", "const id = pm.response.json().id; if (id) pm.collectionVariables.set('assetId', id);"],
      }),
      req('Cancel a multipart upload', 'DELETE', '/cms/assets/uploads/{{uploadId}}', { note: 'Throws the parts away. Start a new upload first; a finished one cannot be cancelled.' }),
      req('Follow the file', 'GET', '/cms/assets/{{assetId}}', { note: 'quarantined, clean or rejected, with the scanner\'s verdict. Never the bytes.' }),
    ], 'cms'),

    folder('4. Files', 'Downloading. Only a clean file exists for users.', [
      req('Download a file', 'GET', '/api/assets/{{assetId}}', { note: 'The bytes. Add a Range header (for example bytes=0-4) to get part of it (206).' }),
      req('Download part of a file', 'GET', '/api/assets/{{assetId}}', { note: 'Range: bytes=0-4 gives the first five bytes with a 206.', headers: [{ key: 'Range', value: 'bytes=0-4' }] }),
      req('Headers only', 'HEAD', '/api/assets/{{assetId}}', { note: 'Size, type and ETag (the file\'s SHA-256) without the bytes.' }),
      req('Delete a file (admin)', 'DELETE', '/api/assets/{{assetId}}', { note: 'Overwrites and removes the stored bytes. Cannot be undone.' }),
    ], 'admin'),

    folder('5. Admin: people', 'Who can sign in. Admins only.', [
      req('List people', 'GET', '/api/admin/users', { note: 'Everyone who can sign in, never their password hashes.' }),
      req('Add a person', 'POST', '/api/admin/users', {
        note: 'Role is User or Admin. Leave password out and one is made and returned once as temporaryPassword.',
        body: { username: '{{newUser}}', role: 'User' },
      }),
      req('Change role or disable', 'PATCH', '/api/admin/users/{{newUser}}', { note: 'Send role, disabled, or both. Not the last admin, not yourself.', body: { role: 'User', disabled: false } }),
      req('Reset a password', 'POST', '/api/admin/users/{{newUser}}/password', { note: 'Leave password out to get a generated one. Ends all of that person\'s sessions.', body: { mustChange: true } }),
      req('Sign a person out everywhere', 'POST', '/api/admin/users/{{newUser}}/revoke-sessions', { note: 'Ends every session of that person.' }),
      req('Delete a person', 'DELETE', '/api/admin/users/{{newUser}}', { note: 'Not the last admin, not yourself.' }),
    ], 'admin'),

    folder('6. Admin: running it', 'Settings, storage and the two queues. Admins only.', [
      req('Show every setting', 'GET', '/api/admin/settings', { note: 'Each setting, its value, default, where it came from and what it is for. Secrets only as set or not set.' }),
      req('Show where data is kept', 'GET', '/api/admin/storage', { note: 'Database, asset and key folders with sizes and free space.' }),
      req('Show the batch queue', 'GET', '/api/admin/ingestion', { note: 'Waiting, applying, done and set-aside batches, and the oldest wait.' }),
      req('Retry a failed batch', 'POST', '/api/admin/ingestion/{{batchId}}/requeue', { note: '409 unless that batch failed and was set aside.' }),
      req('List files with their state', 'GET', '/api/admin/assets', { note: 'Every file, newest first. Add ?state=rejected to filter.' }),
      req('Scan a file again', 'POST', '/api/admin/assets/{{assetId}}/rescan', { note: 'Only a clean file can be scanned again (409 otherwise).' }),
      req('Retry a file the scanner gave up on', 'POST', '/api/admin/assets/{{assetId}}/requeue', { note: '409 unless that file\'s scanning was set aside.' }),
      req('Scan again everything an older scanner checked', 'POST', '/api/admin/assets/rescan-outdated', { note: 'Queues every file whose scanner version is not the current one.' }),
    ], 'admin'),

    folder('7. Health', '', [
      req('Is it up', 'GET', '/health', { who: 'none', note: 'No sign-in needed. 200 when the database can be reached.' }),
    ]),
    folder('8. Sign-in', 'Everything else in this collection signs in with Basic credentials on each request; these routes are for a browser-style session. Last, because Change my password changes the password the other requests use: update adminPassword in the environment afterwards.', [
      req('Sign in (session cookie)', 'POST', '/api/auth/login', {
        who: 'none', note: 'Exchanges a username and password for a cookie that Postman keeps. mustChangePassword says if a new password is needed first.',
        body: { username: '{{adminUser}}', password: '{{adminPassword}}' },
      }),
      req('Who am I', 'GET', '/api/auth/me', { note: 'The caller\'s username, role and how they signed in.' }),
      req('Change my password', 'POST', '/api/auth/password', {
        note: 'Needs the current password. Change newPassword before sending.', headers: [SCRIPTED],
        body: { current: '{{adminPassword}}', newPassword: 'change-me-to-something-long' },
      }),
      req('Sign out', 'POST', '/api/auth/logout', { who: 'none', note: 'Ends the session cookie. A call that changes something with a cookie must send X-Requested-With.', headers: [SCRIPTED] }),
    ]),

  ],
  variable: [
    { key: 'eventId', value: 'postman-demo-1' },
    { key: 'assetKey', value: 'hello-from-postman.txt' },
    { key: 'newUser', value: 'postman-person' },
    { key: 'batchId', value: '00000000-0000-0000-0000-000000000000' },
    { key: 'entityId', value: 'postman-demo-1' },
    { key: 'assetId', value: '00000000-0000-0000-0000-000000000000' },
    { key: 'partNumber', value: '1' },
    { key: 'uploadId', value: '00000000-0000-0000-0000-000000000000' },
  ],
};

const environment = {
  name: 'Lateral CMS - local',
  values: [
    { key: 'baseUrl', value: 'http://localhost:5190', type: 'default', enabled: true },
    { key: 'adminUser', value: 'admin', type: 'default', enabled: true },
    { key: 'adminPassword', value: '', type: 'secret', enabled: true },
    { key: 'userUser', value: '', type: 'default', enabled: true },
    { key: 'userPassword', value: '', type: 'secret', enabled: true },
    { key: 'cmsUser', value: 'cms-ingest-svc', type: 'default', enabled: true },
    { key: 'cmsPassword', value: '', type: 'secret', enabled: true },
  ],
  _postman_variable_scope: 'environment',
};

mkdirSync(out, { recursive: true });
writeFileSync(join(out, 'Lateral-CMS.postman_collection.json'), JSON.stringify(collection, null, 2) + '\n');
writeFileSync(join(out, 'Lateral-CMS-local.postman_environment.json'), JSON.stringify(environment, null, 2) + '\n');
const count = (items) => items.reduce((n, i) => n + (i.item ? count(i.item) : 1), 0);
console.log(`${count(collection.item)} requests in ${out}`);
