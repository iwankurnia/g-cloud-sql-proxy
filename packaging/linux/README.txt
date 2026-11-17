Cloud SQL Proxy Manager for Linux
=================================

Nothing to install: the program carries its own .NET runtime and Google's
cloud-sql-proxy. It needs a desktop (X11 or Wayland via XWayland) and the usual
desktop libraries, which every Ubuntu, Fedora or Mint desktop already has.

Run it from here:

    ./CloudSqlProxyManager

Or put it in your app menu (your user profile only, no root):

    ./install.sh              # ./install.sh --uninstall removes it again

Where things go:

    ~/.config/CloudSqlProxyManager/         saved connections
    ~/.local/share/CloudSqlProxyManager/    the unpacked cloud-sql-proxy

The "port in use by ..." hint names the owning program through `ss`
(iproute2), which is installed on every mainstream distribution.
