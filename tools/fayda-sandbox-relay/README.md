# Fayda sandbox relay (dev only)

The shared VeriFayda sandbox test client has a single registered redirect,
`http://localhost:3000/callback`. This zero-dependency Node server listens there on the
developer's machine and answers every callback with a redirect to the WeredaConnect app's
own Fayda callback, with `code` and `state` (or `error` and `error_description`) intact.

```bash
cd tools/fayda-sandbox-relay
node relay.js                                                     # -> weredaconnect://fayda-callback
node relay.js --target http://localhost:8080/fayda-callback.html  # local web app
adb reverse tcp:3000 tcp:3000                                     # Android emulator or USB device
npm test
```

`sandbox-login.js` signs a documented test persona in at the sandbox the way the Fayda
page does and prints a fresh authorization code (with its PKCE verifier and nonce) for the
server's manual live check, `FaydaSandboxLiveTests`:

```bash
export FAYDA_SANDBOX_PRIVATE_KEY_B64='<test client 1 PRIVATE_KEY, base64 JWK>'
eval "$(node sandbox-login.js --fin 3126894653473958)" && (cd ../../server && dotnet test --no-restore --filter FaydaSandboxLive)
```

The server side of this is `faydaMode: 'sandbox'` (`-c faydaMode=sandbox` on a dev deploy).
Full runbook, test personas and limits: `specs/features/fayda-mock-server.md`, "Sandbox mode".
