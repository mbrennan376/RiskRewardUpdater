RISK/REWARD HOMESEER MONITOR
===========================

Run Install.cmd on the HomeSeer HS4 computer.

The installer uses this HomeSeer HS4 installation folder:
  C:\Program Files (x86)\HomeSeer HS4

If Windows denies access, right-click Install.cmd and choose Run as
administrator. Disable the Risk Reward Site Status plugin before reinstalling
an updated copy so that Windows does not lock its files.

After installation, enable Risk Reward Site Status from the HS4 Plugins page.
It polls the public Risk/Reward data and price JSON files every five minutes and
creates two read-only features if they do not already exist:

  Risk/Reward Last Updated
  Risk/Reward Current Status

No API keys or other settings are required.
