# CurseForge support

UN_Nexo's CurseForge provider is designed so the public desktop binary does not contain a shared private API key.

## Direct API mode

Set `UN_NEXO_CURSEFORGE_API_KEY` in the environment **before** starting the launcher. The value is read at runtime. UN_Nexo does not intentionally persist it to launcher settings, account storage, logs, diagnostics, or release artifacts.

The key is attached only to requests sent to the official `https://api.curseforge.com/v1/` API origin. It is never attached to mod downloads from the CurseForge CDN.

## Server-side proxy mode

Set:

```text
UN_NEXO_CURSEFORGE_API_BASE=https://<your HTTPS endpoint>/<optional path>/v1/
```

A custom base URL must:

- be absolute HTTPS;
- use the default TLS port;
- contain no URL credentials, query, or fragment;
- expose the CurseForge v1 response shapes used by the launcher.

When this variable points to a non-official origin, UN_Nexo treats it as a credential-holding proxy and does not send `x-api-key` to it. A Cloudflare Worker can therefore keep the CurseForge key in a Worker Secret and add the header only when forwarding to CurseForge.

API redirects are rejected so credentials cannot cross origins unexpectedly.

## Download safety

CurseForge file downloads are separate from API requests. UN_Nexo:

- accepts only HTTPS `forgecdn.net` hosts and their subdomains;
- validates every redirect target before following it;
- never sends the CurseForge API key to the CDN;
- enforces a 512 MiB file limit;
- checks the declared file size;
- verifies the SHA-1 supplied by CurseForge metadata;
- stages into a Nexo-created, non-reparse temporary directory;
- publishes a complete dependency batch atomically through `InstanceModService`.

If CurseForge does not provide a third-party download URL for a file, UN_Nexo reports that the file must be obtained through its project page rather than constructing or bypassing a restricted URL.

## Dependencies and installed-file matching

Required CurseForge dependencies feed the provider-neutral dependency planner and are installed before the selected root mod. Optional dependencies are shown but are not installed automatically. Incompatible relations are checked against the planned and already-installed final mod set.

Installed JAR matching uses the CurseForge fingerprint endpoint. Nexo excludes linked/reparse JARs before hashing so a link cannot make it fingerprint an arbitrary file outside the instance.
