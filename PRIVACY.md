# Privacy — UN_Nexo 0.9.0-download-only

UN_Nexo is maintained independently by Yannick Fan / yannickfan67-ai. Contact: https://github.com/yannickfan67-ai/UN_Nexo/issues .

The current application downloads and manages game files. It has no login or account page, does not request Microsoft/Xbox/Minecraft authentication tokens, and does not read old account stores or MSAL caches. Existing account files from earlier releases are left untouched and are not part of exported reports.

Network requests go to official Minecraft/Mojang delivery endpoints. If the user selects BMCLAPI, download metadata/files may also be requested from that service. Those servers necessarily receive the requesting IP address, URL and the application's User-Agent. UN_Nexo does not operate a telemetry, advertising, analytics or credential-collection backend.

Version catalog cache, instance metadata, download queue and game files are stored locally in the UN_Nexo data directory shown in the application. Pending tasks recover paused on restart. Reports are written only when requested and include version IDs, task state and timestamps; local paths, account data, credentials, personal instance names, raw error text and game files are excluded. Sharing a report is the user's choice.

Third-party services have their own privacy policies. Minecraft game files and trademarks belong to their respective owners. Previous versions' authentication implementation is retained only as unshipped development source in this repository; its historical privacy description does not describe the current download-only application.
