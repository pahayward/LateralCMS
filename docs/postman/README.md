# Postman collection

A request for every route of the Lateral CMS API, so you can play with it.

1. In Postman, **Import** both files here: `Lateral-CMS.postman_collection.json` and `Lateral-CMS-local.postman_environment.json`.
2. Pick the **Lateral CMS - local** environment (top right) and fill in the passwords. They are left empty on purpose: `adminPassword`, `cmsPassword`
   and, if you want to try the user routes, `userUser` and `userPassword`. For a development run they are in
   `src/Lateral.Cms/App_Data/dev-credentials.json`; for a real install see [OPERATIONS](../OPERATIONS.md). `baseUrl` is `http://localhost:5190`; change it to match.
3. Send the folders in order. Each request saves the ids it gets back (`batchId`, `entityId`, `assetId`, `uploadId`) for the next one.

The same API is described, and can be tried, in the browser at `<baseUrl>/swagger`.

Notes: sign-in is limited to ten failed attempts a minute, so a wrong password repeated quickly gets a `429`; the last folder (Sign-in) is last because
*Change my password* changes the password the other requests use. The files are made by `node tools/postman/build.mjs`; edit the request table there, not the JSON.
