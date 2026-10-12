MooT — PHP deployment for PS.kz

Requires PHP 8.2+ with curl, openssl, mbstring and PDO SQLite. Use PHP 8.4 on this subscription.
Extract at subscription home, not inside httpdocs: httpdocs/ contains public pages; moot-private/ is its private sibling.
Set alem_key, google_id and google_secret in moot-private/config.php. Do not publish this file or any SQLite database.
Google OAuth redirect: https://cultmoot.online/auth/google/callback
Origin: https://cultmoot.online

No Python daemon or Docker is required. SSE responses are bounded and reconnect rather than occupying a PHP worker indefinitely. This changes delivery timing versus the resident Python server; real Windows-agent reconnect behavior still requires an end-to-end check.

Local integration checks passed: account isolation, CSRF, one-use pairing, agent consent, telemetry, command queue, validated results, event streams, installer SHA-256 and revocation. Browser layouts tested at 390/768/1440 pixels.
Live Google OAuth and AI requests are unverified until owner credentials are configured. Real PC operations were not executed. This is not a claim of complete feature parity under real hardware/load.
Test state.sqlite is excluded from the deployment archive. Do not upload the development database.

The original Python application is preserved in the parent folder. This PHP deployment uses a new empty database and does not import existing Python sessions/history.
