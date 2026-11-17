Cloud SQL Proxy Manager for macOS
=================================

Drag "Cloud SQL Proxy Manager.app" to Applications (or run it from anywhere).
It carries its own .NET runtime and Google's cloud-sql-proxy; nothing else is
needed. Pick the build for your Mac: osx-arm64 for Apple Silicon (M1 and
later), osx-x64 for Intel.

First start: the app is not signed with an Apple developer certificate, so
macOS will say it "cannot be opened because the developer cannot be verified".
Either right-click the app and choose Open (once), or clear the download flag:

    xattr -dr com.apple.quarantine "/Applications/Cloud SQL Proxy Manager.app"

Where things go:

    ~/Library/Application Support/CloudSqlProxyManager/   saved connections
    ~/Library/Application Support/CloudSqlProxyManager/   the unpacked cloud-sql-proxy
