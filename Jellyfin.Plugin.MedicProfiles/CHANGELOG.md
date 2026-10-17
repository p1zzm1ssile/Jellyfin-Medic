# Medic Profiles changelog

All notable changes to Medic Profiles are recorded here. The newest version is at the top.
Jellyfin Medic and Medic Picks have their own changelogs.

## [1.1.5] – 2026-10-17

### Fixed
- **API keys and the qBittorrent password only go to the addresses you saved.** When the Sonarr, Radarr or qBittorrent address answered with a redirect to another address, the call followed it, sending the API key along, and for a 307 or 308 the qBittorrent sign-in posted the user name and password again to the new address. Redirects are no longer followed for these calls. The call fails with a message naming where the address points, so you can save that address instead.
- **Import by hand leaves rejected files unticked.** Every matched file started ticked, including ones Sonarr or Radarr had rejected (as "not an upgrade", say), and a manual import doesn't check them again, so pressing Import could replace a better copy in the library. Rejected files now start unticked, with the reason shown, and can still be ticked to import them anyway.
- **"HEVC plays fine here" needs real playback.** The tip took an empty transcode log as proof that HEVC plays everywhere, so it showed a few minutes after installing. It now waits until the log holds at least 20 transcodes, and no longer lists 4K profiles, where nearly every release is HEVC.
- **Advice follows its evidence.** Any transcode for bitrate led to "untick Remux"; now only transcodes of Remux-sized files (25 Mbps or more) do. The DTS and TrueHD advice no longer reaches profiles that only allow Remux, since a Remux always keeps the disc's own audio.
- **The Blocked tab says when it shows only the newest 100.** With more blocked releases than that in Sonarr or Radarr, the tab now says how many there are in all.
- **The advice report doesn't keep errors.** A report with an error was kept for an hour, and saving the settings didn't clear it. Reports with errors aren't kept now, and saving the settings starts a fresh one.
- **Odd answers and failed saves give a clear error.** A Sonarr or Radarr answer that wasn't the expected JSON made the page fail with a server error; it now says what came back. Saving writes the keys and password before the new addresses, so a failed key write leaves the old settings in place, instead of pairing the old key with the new server.
- **Changing the qBittorrent address forgets what was added to the old one.** The record of which exclusions Medic Profiles added was kept when the address changed, so Stop blocking on the new qBittorrent could remove entries its owner had added there.
- **An empty library search no longer fails.** Clearing the search box and searching answered with an error.
- **A failed save changes nothing.** When the keys were written but the new addresses then couldn't be saved (a full disk, say), a key typed for a new server stayed paired with the old address; when one key couldn't be written, the password or key written before it stayed saved, although the page was told nothing had changed. The keys and password written by that save are now put back as they were, the addresses stay, and the page shows what the server said went wrong instead of only "Couldn't save. Try again.".
- **Choosing the film or series for a rejected file leaves its box as it was.** Picking a match in Change film or Change series marked the file for import while its box still showed it unticked, so Import sent a file the list said it wouldn't. Picking a match now changes only the match: a rejected file is imported only once its box is ticked, and the "Left unticked" note goes away once it's ticked.
- **An empty library search doesn't fetch the library.** A search with nothing to match (blank, or only punctuation) still fetched Sonarr's or Radarr's whole film or series list (unless it had in the last 10 minutes) before answering with no results. It now answers at once.
- **Remux files without bitrate tags, a save during a report, and what an odd answer held.** A Remux whose streams carry no bitrate (common in MKV) read as a small file, so its transcodes didn't count toward the Remux advice; the file's overall bitrate now counts too. A report already being made when the settings were saved could still be kept for an hour afterwards; it isn't now. An answer that wasn't the expected JSON now says what came back ("a list where an object was expected", say) when reading the queue, blocked releases, the library, episodes, indexers or quality profiles.

Thanks @camarigor for reporting these (#159, #206).

## [1.1.4] – 2026-10-17

### Changed
- **A refused qBittorrent sign-in is only tried again when you ask.** Since 1.1.3, saving the settings, Test, Remove key, Block and Stop blocking each tried a refused password again at once. qBittorrent bans the server's address after 5 failed sign-ins by default, so a few ordinary clicks with a wrong password got Medic Profiles banned. Those buttons no longer sign in after a refusal. A new password typed in Medic Profiles is still tried at once. If you fixed the password in qBittorrent, or its ban has ended, the new **Try qBittorrent again** button signs in exactly once per click. After one refusal Medic Profiles waits 5 minutes before trying the same details again; after two it stops until the password changes or you press the button.

### Fixed
- **An error answer from Sonarr or Radarr is never taken for them being out of reach.** One that came as a list without the usual error entries made the Downloads tab say Sonarr or Radarr couldn't be reached. It now says the status they answered with ("Sonarr answered with 400.", say).
- **The action history survives a damaged file.** A history file that couldn't be read was written over by the next action on the Downloads tab, losing every earlier action in it. It's now kept beside it, as the keys file already was, and a new history starts.
- **Each damaged file is kept.** A file set aside is now named with the time (`secrets.json.20261016-153000.bad`, say), so a second damaged keys file no longer replaces the first `secrets.json.bad`.
- **Saved files reach the disk before they replace the old ones.** The keys file and the action history are written to a temporary file and then swapped in; that file is now flushed to disk first, as Jellyfin Medic's own files are, so a power cut at the wrong moment can't leave an empty file in its place on file systems that would otherwise allow it.
- **The Indexers tab shows which indexers are failing.** It asked Sonarr and Radarr for a status list only Prowlarr has, so every enabled indexer showed as working, even while paused after failures. Failing indexers now come from Sonarr's and Radarr's own health checks, in any interface language, and the rest show as enabled. How long an indexer stays paused isn't shown any more, as neither app reports it.
- **Test and Import check they reached Sonarr or Radarr.** Anything that answered at the saved address was taken for Sonarr or Radarr, so a proxy's sign-in page, or Radarr at Sonarr's address, passed the Test, and an import sent on by a redirect said it was importing when nothing was. Test now passes only when the app itself answers, and fails on any redirect, naming the address it went to. Other calls fail when a redirect turned them into a different request. A "not found" that didn't come from Sonarr or Radarr now says to check the address and URL base (/sonarr, say) instead of saying the item may have been removed already.
- **The Downloads tab no longer waits for Sonarr before asking Radarr.** Both are asked at once, so a slow one doesn't hold up the other's downloads. One that takes too long now says it didn't answer within 30 seconds, instead of that it couldn't be reached.

## [1.1.3] – 2026-10-15

### Fixed
- **Signing in to qBittorrent 5.2 works.** qBittorrent 5.2 changed how it answers a sign-in: it now sends back no text when the password is right, where older versions said "Ok.". Medic Profiles took that as a wrong password, so it kept saying the user name and password weren't accepted and waited 5 minutes before trying again, however often the details were re-entered. Both kinds of answer are now understood, as is 5.2's way of saying a password is wrong.
- **Saving the settings tries qBittorrent again straight away.** After a refused sign-in, Medic Profiles waited 5 minutes before trying the same details again, even after the password was fixed in qBittorrent. Saving the settings (or pressing Block or Stop blocking) now tries again at once. If qBittorrent has banned the server's address after too many wrong passwords, the page says so and how to clear it.

Thanks @camarigor (#119), who found and fixed the qBittorrent 5.2 sign-in first; the fix came in through #122.

## [1.1.2] – 2026-10-14

### Added
- **Remove can leave the download in the client.** The Remove panel has a new tick box, on by default. Untick it and the download stays in the download client, still seeding, while Sonarr or Radarr stops tracking it, as their own "Ignore download" does. Handy for a finished download that couldn't be imported.

### Fixed
- **The page says when a list can't be loaded.** The Blocked, Indexers, History and Settings tabs stayed blank or half filled when Medic Profiles didn't answer, and removing a saved key showed nothing if it failed. Each now says so. The Downloads tab's message for this no longer starts with "Radarr:".
- **The keys file is now private.** `medic-profiles/secrets.json`, which holds the Sonarr and Radarr API keys and the qBittorrent password, could be read by any account on the server. It's now only readable by the account Jellyfin runs as, and an existing file is tightened the next time it's read. Windows is unchanged, as it uses its own folder permissions.
- **Saved keys stay with their server.** Changing Sonarr's, Radarr's or qBittorrent's address kept the saved key or password, and the next test or list sent it to the new address. When the address now points at a different server, the saved key or password is cleared and has to be typed again. A keys file that can't be read is also kept as `secrets.json.bad` instead of being replaced by one holding only the newest key (the other keys used to vanish without a word).
- **Blocking files in qBittorrent keeps your own list as it was.** Block removed any of Medic Profiles' patterns you had added yourself (`*.zip`, say, with archives left unticked) while saying your entries were kept, and Unblock left the list switched on even if you had it off. Medic Profiles now remembers what it added and whether the list was on: Block only adds what's missing, and Unblock removes only what it added and switches the list back on or off as it was. The settings page now also says that qBittorrent applies the list to every torrent, not only films and series.
- **Fewer sign-ins to qBittorrent, and no ban from a wrong password.** Every time the Settings tab opened, Medic Profiles signed in to qBittorrent again and never signed out. With a wrong password saved, each visit counted as a failed sign-in, and qBittorrent blocks the server's address after a few. It now keeps one session (and signs it out when the address or login changes), tries a refused password only once every 5 minutes, and says when qBittorrent answered but sent no settings instead of suggesting an update.
- **Custom format advice no longer points at the wrong format.** Each piece of advice named the first custom format whose name matched a broad pattern, and listed every profile that didn't penalise that one format. With TRaSH's formats it could tell you to refuse "DV HDR10" (which has the fallback) or "TrueHD ATMOS" (lossless audio you had scored up), even where "DV (w/o HDR fallback)" and "DTS" were already refused, and it suggested holding back HEVC in 4K profiles too. Advice now looks at every matching format: profiles that already score one down are fine, profiles that score one up made a choice it won't overrule, and the Dolby Vision advice only matches formats for Dolby Vision without a fallback. The HEVC advice leaves out profiles that allow 2160p.
- **A long download queue says when it's cut short.** Medic Profiles reads up to 1,000 queue entries from each app and didn't say when there were more. The Downloads summary now says how many of how many are shown.

## [1.1.1] – 2026-10-11

### Added
- **Contact tab.** Report a problem on GitHub (the new issue is already titled for Medic Profiles), see the open issues, or email p1zzm1ssilerepo@gmail.com for anything private.

## [1.1.0] – 2026-10-10

### Added
- **Block unsafe files in qBittorrent.** Under Settings, add qBittorrent's Web UI address and sign-in, then press **Block unsafe files**: qBittorrent then skips program files (.exe, .bat, .scr, .msi and similar) that come inside film and series torrents, so they never reach your disk. It uses qBittorrent's own "Excluded file names" list (version 4.6 or later) and keeps anything you'd added there yourself. Archives (.rar, .zip, .7z) can be blocked too, but that's off by default, as many genuine Usenet and scene releases come packed and Sonarr and Radarr unpack them. Sonarr and Radarr can't do this themselves, as they only see release names, not the files inside. Other download clients aren't supported yet.
- **Indexers tab.** Every indexer Sonarr and Radarr search, and whether it's working: failing (since when, and when Sonarr or Radarr will try it again), working (and whether it's used for RSS, automatic or manual searches), or switched off. Indexer warnings from Sonarr's and Radarr's own health checks are shown at the top. Indexers added by Prowlarr show here too, as Prowlarr adds them to Sonarr and Radarr.

## [1.0.1] – 2026-10-06

### Fixed
- **Profiles.** The "HEVC plays fine here, which saves space" tip now only appears for profiles that actually give an x265 / HEVC custom format a negative score, and names that format. It used to list every profile, even ones already scoring x265 at 0 or above.

## [1.0.0] – 2026-10-06

### Added
- First release. Sonarr and Radarr from your Jellyfin dashboard, under **Plugins → Medic Profiles**. Admins only.
- **Downloads.** Everything Sonarr and Radarr are downloading in one list, with progress and when it should finish. A season pack is one row, not one per episode. Problems come first, with Sonarr's or Radarr's own explanation: couldn't be imported, failed, unwanted files (such as a .exe), not matched to anything, and downloads with no progress after a few hours (6 by default). Updates every 30 seconds.
- **Remove**, or **Remove and block**: takes it out of Sonarr or Radarr and the download client. Blocking stops that release ever being grabbed again and, unless you untick it, searches for a different one straight away. Every action asks first.
- **Import manually.** For downloads that finished but weren't imported: see each file, what Sonarr or Radarr thinks it is, and why it refused. Change the film, series or episodes if the match is wrong, choose move or copy, and import. Only files that are actually in that download can be imported.
- **Blocked.** The releases Sonarr and Radarr won't grab again, with Unblock.
- **Profiles.** Read-only advice on your quality profiles and custom formats:
  - From what your server plays (using Jellyfin Medic's record of transcoded files, if Medic is installed): avoid DTS- or TrueHD-only audio, HEVC or AV1 when your devices can't play them, Dolby Vision with no HDR10 fallback, 4K for devices that can't show it, and Remux when its bitrate is too high to stream.
  - From the profiles themselves: cinema recordings and unknown quality allowed, minimum custom format scores nothing can reach, custom formats that do nothing, and profiles nobody uses.
  - Each suggestion says which profiles, what to change, why, and what was seen on your server.
- **History** of what was removed, blocked, unblocked or imported from here, and by whom.
- The Sonarr and Radarr API keys are kept on the server in their own file, readable only by admins, and never sent to a browser.
