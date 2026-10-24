# Current product scope and future authentication

## Current distributed version

UN_Nexo 0.9.0-download-only is a download and file-management utility. It downloads version metadata, the client, libraries, native archives and assets from the official delivery endpoints; an optional BMCLAPI source retains official fallback and verification.

It does not authenticate users, create offline profiles, launch the game, connect to servers, or read saved account credentials. The distributed desktop references `UN.Nexo.Downloads`, whose explicit source allowlist excludes the earlier authentication and game-execution implementation. `UN.Nexo.Downloads.Tests` checks that compiled boundary. There is no reviewer-specific behavior, remote activation, or user setting to enable gameplay.

The repository retains previous development code and historical releases; these must not be confused with the current product. Older release binaries are not retroactively modified by this update.

## Purpose of an AppID request

The registered UN_Nexo Client ID is `ac6485d3-1fd4-42d1-89c0-40ffee68d915`. The request concerns a future release that could restore authentication and gameplay only after the required review and implementation acceptance.

The proposed flow is Microsoft public-client OAuth through the system browser, then Xbox Live/XSTS and Minecraft Services. A future gameplay release must require successful Minecraft entitlement and Java-profile checks before enabling play or server access. No offline account fallback, substitute launcher Client ID, borrowed token, or authentication bypass is planned. This document does not claim that the current download-only build performs ownership checks; it offers no gameplay at all.

Avero 0.2.0-download-only is an independently packaged Android companion, with source at https://github.com/yannickfan67-ai/Avero-Launcher. It currently has no compiled authentication implementation and no configured application Client ID. Approval of the UN_Nexo AppID must not be presented as approval of an unregistered Android AppID.

Official review route: https://aka.ms/mce-reviewappid . A request or submission does not mean approval.
