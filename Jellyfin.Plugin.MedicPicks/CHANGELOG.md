# Medic Picks changelog

All notable changes to Medic Picks are recorded here. The newest version is at the top.
Jellyfin Medic has its own changelog in the main CHANGELOG.md.

## [1.2.6] – 2026-10-17

### Changed
- **Accounts with parental controls get Discover picks only once you tick them.** Discover suggests titles from TMDb, which a maximum rating or blocked and allowed tags can't filter, but every account had Discover until an admin unticked it. An account with any of those controls is now left out of Discover, and of requesting from the picks page, until you tick it under "Who gets Discover picks", where it's marked as having parental controls. Accounts without them are as before.
- **A person's picks are rebuilt at most every 20 seconds.** Pressing "Update my picks" or "Show me different ones" again and again started a rebuild each time, each reading the whole library. A rebuild now starts at least 20 seconds after that person's last one; choices made meanwhile are saved and go into it, and the page waits for it as before.

### Fixed
- **The Seerr key only goes to the address you saved.** When the Seerr address answered with a redirect to another address, the call followed it and sent the API key along. Redirects are no longer followed for calls to Seerr. Test now fails with a message naming where the address points, so you can save that address instead, and My requests says Seerr is out of reach.
- **"On your server" leaves out films and series you've started.** Linked picks already did; "On your server" still picked a film you'd stopped half way through, and a series with an episode under way. Both are left out now.
- **The picks playlist keeps its place in your apps.** Each build deleted the playlist and made a new one, so it got a new id, and when making it failed there was no playlist at all. It's now updated in place, and only made again when that fails. Hiding a title on the picks page also takes it out of the playlist straight away, instead of at the next build.
- **The Play link in My requests checks library access.** It checked parental limits only, so a title that arrived in a library the person can't open still got a Play link. It now checks the library too.
- **My requests loads faster when Seerr is slow.** Titles were looked up one after another, up to 30 at 10 seconds each, and a title Seerr couldn't give was asked for again on every visit. Up to 4 are now looked up at once, a failed one isn't asked for again for 5 minutes, and titles are kept per Seerr server.
- **Builds read the library less.** "On your server" and Linked picks each read the films and series they choose from; they now share one reading per build, and without Discover the library isn't read for the TMDb ids only Discover uses. Linked picks also match names through a list made once per build instead of comparing every title watched with every title in the library.

Thanks @camarigor for reporting the Seerr key redirect (#206).

## [1.2.5] – 2026-10-17

### Fixed
- **The saved Seerr key stays with its server.** Changing the Seerr address kept the saved key, and the next request, test or list sent it to the new address. When the address now points at a different server (another scheme, host or port), the saved key is removed and has to be pasted again, as Medic Profiles already does with its keys; a new path on the same server keeps it. The settings page now saves a key typed next to the new address after the address, so the new key is the one kept.
- **A TMDb that stops answering no longer ends the nightly run.** When TMDb didn't answer, the call gave up in a way the nightly picks run took for being cancelled, so the run stopped at that person and everyone after them kept their old picks, library picks included. A TMDb call now gives up after 20 seconds and counts as TMDb being unavailable, as other TMDb failures already do: the run stops asking TMDb, keeps everyone's previous Discover picks and carries on with their library picks.
- **My requests says Seerr is out of reach when something else answers its address.** A sign-in page in front of Seerr, or anything else answering its address with a web page, made My requests fail with a server error. That answer now counts as Seerr being out of reach: the page says so, and the log has a warning saying what kind of answer came back.
- **My requests no longer fails on a season entry it can't read.** A Seerr request whose list of seasons held something other than season entries (a bare number, say) made My requests, and the admins' list of requests waiting for approval, fail with a server error. Such entries are now skipped, as Medic Profiles already does with what Sonarr and Radarr send, and the seasons that can be read still pick which downloads count.
- **Choices on the My picks page survive a damaged file.** A person's choices file that couldn't be read was written over by their next change (a Hide, say) or the nightly rebuild, so the titles they had hidden came back. It's now kept beside it, as the keys file already was, and new choices start.
- **Each damaged file is kept.** A file set aside is now named with the time (`secrets.json.20261016-153000.bad`, say), so a second damaged keys file no longer replaces the first `secrets.json.bad`.
- **Saved files reach the disk before they replace the old ones.** Picks, choices and the keys file are written to a temporary file and then swapped in; that file is now flushed to disk first, as Jellyfin Medic's own files are, so a power cut at the wrong moment can't leave an empty file in its place on file systems that would otherwise allow it.
- **An episode with no series no longer stops a person's picks from updating.** Jellyfin makes a video at the root of a TV library an episode that belongs to no series, and building the picks of anyone who had watched one failed: they kept their old picks (someone new got none), and only the log said why. Such episodes are now left out of what counts as watched. The My picks page also said "Updated just now" after an update that had failed; it now says the picks couldn't be updated, and shows them as they were.
- **"Dubbed audio only" knows every language Jellyfin does.** It knew 16 languages and took any other TMDb language for English (Turkish, say, or Norwegian set as nb-NO), so it kept titles with English audio and the page said English. The language now comes from Jellyfin's own list of languages, with every code that list has for it: a Turkish dub is found whether its track says tr, tur or Turkish. Any Norwegian code counts as Norwegian (Jellyfin lists no, nb and nn apart), a track tagged with a region (en-US, say) counts as its language, and a language missing from Jellyfin's list matches only its own code instead of English.
- **Seerr accounts are looked up again on a new Seerr.** Medic Picks remembers each person's Seerr account for 10 minutes, but not which Seerr it came from, so after the address moved to another Seerr (and its key was saved) requests and approvals could be made as whoever had that account number there, and My requests could show their requests. Remembered accounts now belong to the address they were found at.
- **A Seerr address saved without http:// works.** An address such as `seerr:5055` was saved as typed, and My requests then failed with a server error. Both Seerr addresses now get `http://` added when the settings are saved. An address saved earlier without it makes My requests say Seerr can't be reached until the settings are saved again.
- **Discover picks go away as soon as Discover is off.** Unticking someone under "Who gets Discover picks", or switching Discover off for everyone, only took effect at the next build, so until then My picks kept showing the Discover picks saved at the last one, with their Request buttons. My picks now checks both settings every time it loads, requests from the page are refused while Discover is off for the whole server too, and any change to who gets Discover picks, either way, rebuilds the picks of the people it affects straight away.
- **My picks only sends the Seerr address when it links to Seerr.** It was sent to everyone's browser even when the page makes requests itself or links to TMDb, where nothing uses it.

## [1.2.4] – 2026-10-14

### Added
- **A Seerr address for viewers.** If this server reaches Seerr at an address people can't open (http://seerr:5055 inside Docker, say), put theirs in the new "Seerr address for viewers" box: links to Seerr use it, while Medic Picks keeps using the other one itself.

### Fixed
- **The settings page says when saving or removing a key fails.** Saving or removing the Seerr or TMDb key, and Build picks now, showed nothing when the server didn't take it. Each now says so, and a TMDb key that isn't saved no longer leaves Discover picks switched on.
- **The keys file is now private.** `medic-picks/secrets.json`, which holds the TMDb and Seerr keys, could be read by any account on the server. It's now only readable by the account Jellyfin runs as, and an existing file is tightened the next time it's read. Windows is unchanged, as it uses its own folder permissions.
- **"My requests" checks each request is yours, and Discover-off accounts can't request.** The list relied on Seerr filtering requests by account; it now also checks every request against the person's own Seerr account, so a Seerr version that ignores the filter can't show anyone else's. Accounts with Discover turned off (a child's, say) could still request any title through the page's request call; that's now refused.
- **Approving and declining now happen as you in Seerr.** They were sent with only the Seerr API key, which Seerr treats as its owner account: any Jellyfin admin could approve or decline (4K requests included) even without permission to manage requests in Seerr, and Seerr recorded its owner as the one who did it. Medic Picks now finds the admin's own Seerr account and acts as it, as it already did for requests; an admin who has never signed in to Seerr is told to do so first.
- **Saving both keys at once no longer loses one.** The settings page saves the TMDb and Seerr keys together, and each save read the keys file, changed its own key and wrote the file back, so the second could write over the first: one key was then gone without any message (8 times in 20 in a test). Saving is now one locked step. A keys file that can't be read is kept as `secrets.json.bad` instead of being replaced by one holding only the new key, and its permissions are checked once instead of on every read.
- **"My requests" no longer fails when Seerr is down.** With Seerr unreachable, looking up the person's Seerr account wasn't caught, so the page got an error instead of "can't reach Seerr" (and only now and then, as a found account is remembered for 10 minutes). A refused key or a broken Seerr is now also told apart from "never signed in to Seerr". Every call to Seerr gives up after 10 seconds, so a Seerr that hangs can't hold the page open, and a picks rebuild that fails is now written to the log.
- **A TMDb outage no longer empties Discover.** A refused key, a rate limit, a TMDb error or no connection came back as "no recommendations", so the nightly run saved an empty Discover over everyone's previous one, and also cleared the list of titles already shown. Those failures are now told apart from an empty answer: each person keeps their last Discover picks, and after the first failure the run stops asking TMDb for the others.
- **A title you hide stays hidden even if your picks were being rebuilt at the time.** A rebuild, the nightly one included, saved the picks it had worked out with the title still in them, so it came back until the next rebuild. Picks are now saved without anything hidden at that moment, and your choices are changed one at a time, so two changes made together can't undo each other.
- **Linked picks leave out titles you've started.** They only left out what you had finished, so a film you'd stopped halfway through, or a series with an episode under way, could be suggested to you.
- **The My picks page names itself with the plugin's version.** It told Jellyfin it was version 0.0.1, which is what Dashboard and Devices showed for it.
- **Only http and https Seerr addresses become links.** The Seerr address went into every person's Request in Seerr link as it was typed.

## [1.2.3] – 2026-10-11

### Added
- **Contact section** at the bottom of the settings page: report a problem on GitHub (the new issue is already titled for Medic Picks), see the open issues, or email p1zzm1ssilerepo@gmail.com for anything private.

## [1.2.2] – 2026-10-10

### Fixed
- **The Seerr address is saved with the key.** It used to be saved only by the Save button at the bottom of the settings, so pressing Save key next to it lost the address, and Test Seerr then said to add the address and key even though both were filled in. Save key, Test Seerr and Save now all save every setting on the page, including any key you've typed. The Save button is now at the very bottom of the settings page, below the menu link settings.
- **Saving a TMDb key switches on suggestions from outside your library.** Before, "Suggest titles that aren't on the server" had to be ticked separately, so with only the key saved the My picks page showed nothing that wasn't on the server, and no Request buttons. Turning it on now rebuilds everyone's picks straight away, and the settings warn when a key is saved but suggestions are off, or when the Seerr key has no address.

## [1.2.1] – 2026-10-09

### Added
- **Show me different ones.** A new button on the My picks page swaps your picks for the next best ones, so you can cycle through suggestions instead of seeing the same ones every time. When you've been through them all it starts again from the top, and changing your choices starts fresh.

### Fixed
- **Update my picks no longer looks stuck.** With a high count and a genre ticked, the page could come back with only a few picks and no explanation, or stop waiting before the rebuild had finished. It now waits until your picks are really done (showing how long it's taking), says "Updated just now" with how many picks you got, and tells you when only a few titles on the server match your choices.
- **New suggestions from outside your library now appear straight away** after Update my picks, instead of only after reloading the page.

## [1.2.0] – 2026-10-06

### Added
- **My requests.** The My picks page now shows what you've asked for in Seerr and where each one has got to: waiting for approval, approved and looking for a download, waiting in the download queue, downloading (how far it's got, and roughly when it'll be ready), or ready to watch, with a Play button. Series show which episode, or how many, are downloading. Requests made in Seerr itself are included too. Problems show as "there's a problem with the download" rather than a made-up time. The list updates by itself every minute while anything is on its way, and finished requests drop off after two weeks. Each person only ever sees their own requests.
- **Approve and decline from the My picks page.** Admins see everyone's requests waiting for approval at the top of the page, with who asked, and can approve or decline them there.
- Download progress comes from Sonarr and Radarr through Seerr, so they need to be connected in Seerr (Settings → Services). It uses the Seerr key Medic Picks already has; no Sonarr or Radarr keys are needed. Admins can turn it off with "Show people their requests" in Medic Picks' settings.

## [1.1.1] – 2026-10-06

### Fixed
- **1.1.0 didn't include its new features.** The 1.1.0 download was built from the 1.0.4 code by mistake, so the My picks page still showed "Anime: English dubs only" and none of the choices below. 1.1.1 is the real 1.1.0: install it and restart Jellyfin.
- The My picks page is no longer kept in the browser's cache, so a new version shows straight away after an update.

## [1.1.0] – 2026-10-06

### Added
- **Choose what you see.** Everyone's My picks page now has a settings box:
  - **Show:** films and series, films only, or series only.
  - **Genres:** tick as many as you like. Every standard genre is offered (Action, Adventure, Animation, Anime, Biography, Comedy, Crime, Documentary, Drama, Family, Fantasy, History, Horror, Kids, Music, Mystery, Romance, Science Fiction, Sport, Thriller, War, Western), plus any others in your library. Titles matching any ticked genre are shown. Paired TV genres count too, so Science Fiction also finds "Sci-Fi & Fantasy" series.
  - **Seasonal:** Christmas and Halloween, found from tags, genres and titles.
  - **How many:** 5, 10, 15, 20, 25 or 30 picks in each section.
  - **Update my picks** rebuilds your picks straight away with your choices.
- **Linked to what you've watched.** A new section with titles on the server from the same world as something you've watched: a series' films and the other way round (The Seven Deadly Sins series and its films), sequels (Frozen, Frozen II), the rest of a collection, and titles sharing a franchise tag, such as Marvel series and the MCU films. Each says why it's there. Admins can turn it off in the settings.
- **Ignore – don't recommend again.** On any pick, removes it straight away and it's never suggested again. "Show them again" brings ignored titles back.

### Changed
- **"Dubbed audio only" replaces "Anime: English dubs only"** and now covers every title, not just anime. On your server, only titles with audio in your language (set by the TMDb language) are shown. Suggestions not on the server that were first made in another language are marked "audio not confirmed". If you had the old option ticked, the new one is ticked for you.

## [1.0.4] – 2026-10-06

### Fixed
- Duplicate "Picks for you" playlists could pile up if a rebuild was stopped partway, or if two rebuilds ran at once. Picks now saves the new playlist straight away and builds one person at a time.
- Turning Discover off for someone now clears their old Discover picks.
- Saving your preferences repeatedly no longer starts several rebuilds at once.
- Preferences and keys are saved safely, so a crash mid-save can't wipe them.

## [1.0.3] – 2026-10-05

### Fixed
- Seerr and preference handling made more reliable.

## [1.0.2] – 2026-10-05

### Added
- One-tap Seerr requests, made under each person's own Seerr account.
- English dubs for anime.

## [1.0.1] – 2026-10-04

### Added
- A My picks link in everyone's Jellyfin menu, added automatically.

## [1.0.0] – 2026-10-04

### Added
- First release: a personal "Picks for you" playlist in every app, and a My picks page.
