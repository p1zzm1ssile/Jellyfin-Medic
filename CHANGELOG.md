# Changelog

All notable changes to Jellyfin Medic are recorded here. The newest version is at the top.

This project uses [semantic versioning](https://semver.org): given a version X.Y.Z,
Z changes for fixes, Y for new features, X for changes that break compatibility.

Medic Picks has its own changelog: Jellyfin.Plugin.MedicPicks/CHANGELOG.md.

## [1.0.23] – 2026-10-17

### Fixed
- **The Disks panel says when a library drive is missing, instead of charting the drive above it.** When a library folder couldn't be found, as when a USB or network drive didn't mount, Medic read the space of the folder above it, usually the system drive, and charted it as that library's drive, with a forecast that could say "full in under a day". An empty library folder, which a drive that didn't mount often leaves behind, did the same. Those folders are no longer read: the panel marks the drive as missing, with the folder it looked for and its last reading, and the Disks tile says how many drives are missing. Checks already reported the missing folder.
- **Today draws a cancelled run as cancelled.** Runs that LoadGuard or "Stop running tasks" cancelled were drawn like any other run. They now look like stopped runs, with the word "cancelled", and still show when a task runs many times a day.
- **"Fixed it: count again from now" says it worked.** The count was reset, but the page took the server's empty answer as a failure, so nothing changed and the button came back. It now reloads the checks and says the count starts again from now. Pausing and resuming a track run had the same empty answer; it's fixed for those too.
- **"Stop running tasks" asks the server what's running, and names the tasks.** It went by the Dashboard's last reading, which stops while another tab is open, so it could say "No tasks are running" during a library scan. It now asks Jellyfin when you press it, lists the tasks in the confirm, and names the ones it stopped.
- **Times on Medic's page are on the server's clock.** Times such as "When" on the Schedule tab's changes and the runs on History were on your browser's clock, while the schedule itself and Medic's messages use the server's, so the two could disagree in the same row. Every time is now on the server's clock, and when that differs from yours the Schedule and History tabs say so, as Today already did.
- **The keyboard stays where it was when a list is redrawn.** Choosing a block on the Schedule timeline, a task's choice in Preview changes, or Ignore on a finding sent the keyboard back to the top of the page. It now stays on the block or the choice, and goes to the next finding after Ignore. The chosen block on the timeline is also marked by bold underlined text, not only a thin ring.
- **Failed actions give the server's reason.** The page guessed why something failed, for example "A run may already be going" when FFmpeg couldn't be found. It now shows the reason the server gave, and only falls back to a hint when there's none.
- **The Dashboard says when its tiles stop updating.** When Jellyfin stopped answering, the live tiles kept their last readings without a word. A note now says when they stopped updating, and goes once they update again. Finishing the checks also no longer hides an error another action had just shown.
- **A blank "FFmpeg threads" box means automatic.** Clearing it made every save of the track settings fail. Blank now saves as 0, which lets FFmpeg decide, and a saved 0 no longer shows as 1. After saving, the page shows the values the server kept, so a number it brought into range shows as saved.
- **The plugin directory is tried again soon after it couldn't be fetched.** A failed fetch of the community list was kept for 6 hours. It's now tried again after 2 minutes, as links in scripts and themes are.
- **Tasks LoadGuard held back are remembered across a restart, and let go when LoadGuard is switched off.** The list of tasks LoadGuard stopped to start again later was kept only in memory, so a restart of Jellyfin, Medic's own restart tasks included, forgot them. It's now saved, and after a restart LoadGuard starts them again as before, leaving out any stopped over a day ago, which will have had their next scheduled run. Switching LoadGuard off used to freeze the list, and switching it back on days later started those old tasks; now switching it off lets them go, to run at their next scheduled time.
- **Medic's two restart tasks no longer wait for each other.** "Restart Jellyfin for waiting updates" and "Scheduled restart" each waited until no other task was running, so when both ran at once each waited for the other and both gave up after an hour. Neither waits for the other now.
- **A task that runs every few minutes no longer pushes the others out of History.** History kept the newest 2,000 runs of all tasks together, so a task that runs every 5 minutes filled it in about a week. It now keeps up to 300 runs of each task (more than a day of runs every 5 minutes) and 4,000 in all. Runs that finish within a few seconds of each other are also written to disk together, instead of the whole history being rewritten for each one.
- **Medic says when it can't record busy hours, server load or disk space, and keeps damaged records.** These readings failed silently (only in the debug log), and one failing stopped the others recorded with it. Each is now recorded on its own, and a failure is a warning in the Jellyfin log, once until it works again. A busy hours or server load file that can't be read is kept as a .bad file before a new one is started, as the schedule files already were.
- **Track cleanup keeps space free for everything else on the drive.** Each file only checked that its own copy would fit, so with originals kept a long run carried on until about 1 GiB was left, and imports, downloads and Jellyfin's own writes on that drive could start failing. A run now leaves the larger of 10 GiB and 5% of the drive free, and files checked at the same moment can no longer both take the same space. The progress line shows how much space kept originals are holding, since "saved" only becomes free once they're deleted.
- **A file replaced between runs keeps its own original.** When Sonarr or Radarr upgraded a cleaned file under the same name, the next run cleaned the new release and kept nothing of it, because .medic-originals already held the old release's original. Medic now notes what it made of each file, and when the file in the library isn't that, the old original is moved aside under a dated name (`Film (2020).replaced-20261017-093000.mkv`) and the new release is kept.
- **Files left alone aren't counted as failures.** A file skipped on purpose (not enough space, hard links, changed while it was being cleaned) was counted as failed. The progress line now shows them as "left alone", apart from real failures.
- **Track cleanup keeps a record of what it changed.** The run's notes only lived in memory, so after a restart nothing said which files a run had changed. Every changed file is now recorded, with what was removed and whether its original was kept, and "What earlier runs changed" on the Tracks tab shows the newest 200.
- **Kept originals with no file beside them can be deleted.** "Delete the kept originals" only deletes originals whose cleaned file is still under the same name, so originals whose file Sonarr or Radarr had renamed or replaced stayed for good. The Tracks tab now says how many there are, and "Delete those with no file too" deletes them as well.

Thanks @camarigor for reporting these (#154, #155, #156, #157).

## [1.0.22] – 2026-10-17

### Fixed
- **A Dashboard panel that fails no longer stops the others.** The panels were drawn one after another, so data a panel didn't expect left every panel after it on "Loading" and reported the whole load as failed. Each panel now draws on its own and, if it can't, says so in its place. A section the server couldn't build also hides its hints and the time zone note until its data comes back, and Network shows its message as a proper row.
- **Today reads better, above all on a phone.** The lane names sat on the timeline, over the first runs and marks of the day, and a failed or stopped run late in the evening ran off its right edge. The names now sit beside the timeline and late blocks stay inside it. Stopped runs have their own colour and a place in the legend, and the legend says that runs take their task's colour, as on the Schedule tab.
- **Track cleanup keeps a film's own audio when only a commentary is in your languages.** A commentary, audio description, isolated score or karaoke track in a language you keep counted as the film's audio, so a Japanese film with an English commentary kept only the commentary, and lost its subtitles too when none were in your languages. These extras, recognised by their track titles, are still kept or removed by their language, but they no longer stand in for the film's own audio: when they're all that would stay, the film's own audio tracks stay as well, and its subtitles are kept as for any film in another language.
- **Secrets stay hidden in repeated log errors, home page banners and scripts in the support export.** Medic turned digits in a repeated log error into # before hiding secrets, and a secret with # in it no longer looked like one, so Telegram bot tokens, web tokens (JWT) and credentials after Bearer, Basic or Digest that held a digit showed on the Dashboard and in Checks. The banner for a serious error, and its entry in Checks, quoted the log line without hiding anything. The support export only hid a secret inside a script, such as JavaScript Injector's, when it was in double quotes, and missed X-Api-Key headers there, so `apiKey: '...'` or a value in backticks showed. All of these are hidden now. In the export a hidden value keeps its own quotes, and a value without quotes, which in a script is code, is left as it is. A repeated error that held such a secret now reads differently, so if you ignored it, it needs ignoring once more.
- **Missing and disabled Unraid disks are reported.** Medic skipped every disk whose Unraid status contained NP (no device present), so a missing disk (NP_MISSING) or a disabled one with no device in its slot (NP_DSBL) never showed, while a new disk added to an empty slot was reported as failing. Now only a healthy disk, an empty slot and a new disk are left out.
- **Webhook destinations that don't need an address aren't called empty.** Medic counted every Webhook destination without an address as empty, but SMTP and MQTT never use one, and Pushover and Pushbullet send to their own service when it's left blank. Only Discord, Slack, Gotify, Generic and Generic Form destinations are counted now. The "extra text on the end" problem also leaves SMTP and MQTT out now, as they never use the address.
- **On Windows and macOS, the hardware acceleration settings are checked too.** With hardware acceleration on, Medic only checked its settings on Linux: hardware encoding, the formats decoded on the GPU, 10-bit HEVC, HEVC encoding and trickplay encoding. These checks now run on every platform and for every method, AMF and VideoToolbox included, so a Windows or macOS server may see these findings for the first time. Whether Jellyfin can see the GPU, and whether the method suits it, is still checked on Linux only, as Medic can't see the GPU elsewhere. "Tone mapping is off" also no longer shows when tone mapping is on through Intel's VPP, with QSV or with VAAPI on an Intel GPU, or through VideoToolbox. When only one format isn't decoded on the GPU, the finding now says "HEVC isn't decoded on the GPU", so if you ignored it, it needs ignoring once more.
- **An admin's home page banners go away when they sign out.** Jellyfin's web app doesn't reload when someone signs out, so the banners stayed on the sign-in screen, and banners still on their way when an admin signed out could show to whoever signed in next, admin or not. Signing out now removes them, and banners that arrive after a sign-out are dropped. Banners each person dismissed stay dismissed.
- **After a failure, a task you keep stays at its time, and no task is moved to start in the hours you avoid.** When a task fails, Medic moves it 3 hours later until it next succeeds. It did that even to a task you set to "Keep its schedule" in Schedule's "Preview changes", and even when the new time fell inside your "Never schedule tasks between" hours. It also moved the task before writing down its original time, so a task whose move couldn't be added to "Schedule changes" stayed moved for good. Medic now writes both down first, and leaves the task where it is when it can't. A back-off also ends when its task next succeeds even if "Schedule changes" can't be written then, so a later failure can move the task again.
- **Today doesn't count a cancelled run as ran, and keeps tasks that started before midnight.** When a task's only run at its planned time was cancelled (as LoadGuard and "Stop running tasks" do) or stopped, the planned time still said it ran. It now says cancelled, and a later run of the task takes its place, on time or late by when that run started. A task that started before midnight was left out of Today, even while it was still running. It now shows from midnight, and counts for its planned time like any other run.
- **Track cleanup and "Free up disk space" leave links alone.** Track cleanup counted a file's hard links with a `stat` option that macOS and BSD don't have, and cleaned the file whenever the count couldn't be read, so on those systems a file a torrent client seeds took more space after cleaning, not less. It now asks the way macOS and BSD expect when the first way fails, and leaves the file alone, with a note in the run's results, when the count still can't be read. Symlinked files are left alone too, Windows included: cleaning one replaced the link with a full copy (moving the link into .medic-originals when originals are kept) and never changed the file it points to. On Windows the link count still isn't checked. "Free up disk space" no longer follows links inside the folders it cleans, so it can't list or delete files, or remove empty folders, outside them; a transcode folder that is itself a link is still cleaned.
- **Jellyfin's CPU reading no longer drops to 0% when an FFmpeg job ends.** Medic added up the CPU time of Jellyfin and of the FFmpeg processes running at that moment, so when a transcode or another FFmpeg job ended, its time left the total and the next reading could fall to 0%. That also cut spells of heavy use short, so some were never recorded. On Linux, the time of FFmpeg processes that have finished now stays in the total.
- **Heavy use is recorded even when it starts soon after an earlier spell.** After recording a spell of heavy use, Medic waits 15 minutes before recording the same resource again, and a spell that reached 45 seconds during that wait was never recorded, however long it lasted. It's now recorded when the wait is over, if it's still going. A spell that goes on is also recorded again every 15 minutes, so an hour of it counts about 4 times in "Jellyfin ran flat out".
- **The share of playback that's transcoded counts only video being converted, and leaves out paused streams.** Every transcode counted, including those that convert only the audio, and music, though that's light work hardware acceleration doesn't help with; they could push the share past 30%, where Medic flags it and, with hardware acceleration off, tells you to turn it on. A transcode now counts only when its video is converted. Paused streams counted as playback too; now they don't count at all, also in busy hours and "watching at once". Earlier readings stay in the share, so it changes gradually.
- **Track cleanup matches every language code in either form.** Only 16 common languages matched in their other forms, and Jellyfin lists a track tagged "gre" as "ell", so keeping "gre", "tr", "cs" or "he" never matched tracks tagged "ell", "tur", "ces" or "heb"; when none of a film's audio matched, all of it was kept. Every ISO 639-2 code now matches in either 3-letter form and as its 2-letter code, with or without a region such as "pt-BR"; "nob" and "nno" match "nor", and the old codes "iw", "in" and "ji" mean "he", "id" and "yi". Files with a track tagged "gre", "cze", "rum" or another code Jellyfin lists in its other form are no longer skipped as out of date on every run. A code Medic doesn't know is still saved, and a note under "Keep languages" names it, in case it's a typo.
- **The sign-in check reads the whole week, and says when it couldn't.** It read only the newest 500 entries of Jellyfin's activity log while its findings spoke of the last 7 days, and when reading the log failed, or Medic couldn't find it, Security could say "No problems found" and the Dashboard listed nobody signing in from several places. Medic now reads the week 500 entries at a time, up to the newest 10,000 (the findings say so when it stops there), and whenever the log can't be read (it doesn't answer within 15 seconds, fails, or isn't available) it says the sign-ins couldn't be checked, and why. Every address inside your home network, and Tailscale's, now counts as one place, so a TV, a phone and a laptop at home no longer make an account look like it signs in from several places.
- **A monthly task moved to a later day doesn't run twice that month, and Preview lists tasks with no free slot last.** Medic only counted a monthly run on or after the task's current day and time, so a task that had already run that month on its old day ran again on its new, later one, and the calendar on the Schedule tab showed it there. Any run that month now counts, so the task waits for the next month. The same goes for a task made monthly during a month, even when its day that month is still ahead. A monthly task restored from a backup keeps this month's run when its day is still ahead, unless it already ran that month. In Preview changes, a task with no free slot was listed first; it now comes after the tasks Medic planned.
- **Track cleanup starts straight away, and Stop works while it plans.** "Strip tracks for real" and "Dry run (report only)" kept the Tracks tab waiting until Medic had gone through every file in your libraries, and a dry run until it had finished, before it showed the run's progress and its Stop button. Stop also did nothing until that first pass was over. A run now starts in the background, its progress shows at once, and Stop ends it even while it's still going through your files. A run also sticks to the "Don't keep the originals" choice it started with: saving your track settings during a run used to change it for the files not started yet. Track settings you changed but didn't save were ignored by "Scan and preview", "Dry run (report only)" and "Strip tracks for real", which used the saved ones. They're now saved first, after you confirm, and nothing starts if they can't be. The last confirm before stripping now says which languages are kept, what is left alone, and whether the originals are kept.
- **The exposure check recognises Jellyfin with a base URL, and suggests the address you publish.** The check asked for Jellyfin at the root of the address, so a server with a base URL, such as /jellyfin, was reported as something that didn't look like Jellyfin. It now asks under the base URL and, if Jellyfin doesn't answer there, once more at the root, where a reverse proxy may serve it. It also never suggested your domain, because it couldn't read the "Published Server URI" entries in Jellyfin's Networking settings. It now suggests the address published to everyone (all=) or to the internet (external=), with its protocol and port; one published only to your local network (internal= or a subnet) is left out.
- **The exposure check keeps the port and protocol you chose, and says what it tested.** Coming back to the Dashboard, or pressing "Use my IP + 8096", put the suggested port and protocol back over the ones you had picked, so the check could test another port than the one you meant, and its result named only the host and port. Medic's suggestions now fill only the boxes you haven't changed, and every result names the address as tested, such as https://jellyfin.example.com:443/jellyfin, with the base URL when it was used.
- **The banner for a serious error reads the server log, not FFmpeg's.** It read the newest log files of any kind, and while anyone was watching those were FFmpeg's logs for each stream, so a damaged database, a full disk or a failed plugin in the server log went unreported, in the banner and in Checks. It now reads only the server's own logs, the same ones Medic counts repeated errors in.
- **A big drive isn't called full while it still has room.** A drive counted as full below 1% free and nearly full below 3%, so a 16 TB drive with 140 GB free was "full". The share of the drive now stops counting at 50 GB for full and 150 GB for nearly full, with 5 GB and 25 GB as the floor as before, so only drives above about 5 TB see a change. How fast a drive fills is still watched by "will be full".
- **A RAM folder for transcodes isn't reported as a full drive.** A drive that holds only transcodes, such as the 4 to 8 GB RAM folder Medic suggests, was called full or nearly full even when empty, and one reading taken during a transcode could say it would be full within days. It's left to the transcoding checks, which judge its size, and the Dashboard still shows it.
- **On Windows and macOS, low space on the cache drive is reported.** Medic compared the cache and config drives by mount point, which it can't read there, so it took them for the same drive and never checked the cache drive. Drives are now named by their volume there too, and two drives Medic can't name count as different, so the check runs.
- **"Will be full in about 1 day" reads properly.** It said "about 1 days".
- **Jellyfin in a Kubernetes pod is recognised.** Medic only knew a container by Docker's and Podman's marker files, or by "docker" in its control group, and a pod on containerd has neither, so it got the advice for a plain Linux server: the jellyfin service, /etc/fstab and the render group. A pod is now recognised from what Kubernetes gives every pod, even one run by Docker, and its advice speaks of the pod's spec, the GPU's device plugin and an in-memory emptyDir. A plain containerd container outside Kubernetes still counts as Linux, as it leaves nothing reliable to tell it by.
- **An NVIDIA GPU is only reported in a container when it was given one, and a USB disk is no longer called a hard drive on its say-so.** Inside a container Medic took the host's NVIDIA driver for a GPU, so a container with an Intel or AMD GPU, or none, was told to use NVENC, and a container given only its second NVIDIA GPU wasn't seen to have one. A container now needs the GPU's own devices. Many USB enclosures say their disk spins even when it's an SSD, so "Jellyfin's database is on a spinning hard drive" no longer shows for a USB disk. "The server has no name" now also shows in a Kubernetes pod, whose hostname is the pod's name, and its explanation says "In a container" instead of "In Docker".
- **Kept originals and "Remove duplicate files" are safer for Sonarr, Radarr and your films.** The hidden .medic-originals folder, and its .ignore file, were made with Jellyfin's own owner and permissions, so Sonarr or Radarr running as another user couldn't delete a film's folder once Medic had kept an original in it. They now take the owner, group and permissions of the film's folder, the setgid bit included; where Jellyfin isn't allowed to give them the owner, they get the group, and when even that fails a run says so once. "Remove duplicate files" put the copy left by Medic 1.0.4 to 1.0.7 in the film's place without reading it, though those versions could leave out streams. It now reads each copy with FFprobe first and only uses one that has the film's video, some audio and the film's length; otherwise it leaves the film and the copy as they are, and the button says which titles it left and why. Without FFprobe, it tidies nothing, and the button now shows the server's own message when it can't tidy.
- **Secrets stay hidden in a plugin's settings on the Plugins tab and in failed task errors in Checks.** "Show settings" on the Plugins tab hid fewer secrets than the support export, so a script such as JavaScript Injector's showed `apiKey: '...'`, `"apiKey": "..."` or an X-Api-Key header as written, and the value of a header a plugin saves as a name and a value, such as X-Api-Key or Authorization. It now hides everything the export hides, with •••• as elsewhere on that tab, and a long value of letters and digits that looks like a key is hidden there too. A failed task's error in Checks was shown as Jellyfin reported it; credentials in it, such as a token after Bearer or an API key in a web address, are hidden now, before the error is cut to length so half a token can't show.
- **Opening Medic no longer says the checks couldn't run when the browser dropped the request.** When the page loaded twice in a moment, or the connection dropped, the browser gave up on the report and Medic showed "The checks couldn't run", while Jellyfin logged "A task was canceled" as an error, so it looked like Medic had failed. The page now asks once more before showing an error, and a request the browser gives up on is no longer logged as an error. An error from the server still shows straight away. If the report arrives but the page can't show it, it now says so instead of blaming the checks.

## [1.0.21] – 2026-10-15

### Added
- **A fuller Dashboard.** New panels put side by side what Medic already records: today's planned runs next to the ones that ran, with failures, late and missed runs, heavy use and LoadGuard on one axis; the load of the last 10 minutes against the alert levels; busy hours next to the week's task runs; why files are transcoded next to what in the library causes it; each drive's last 30 days and when it would be full; your connection next to the most people watching at once; repeated log errors; and who needs a look among the accounts. On a phone they stack in order of priority. "Today's tasks" and "Last 24 hours" are part of Today now; older runs stay on the History tab. Nothing new is collected or sent anywhere.

### Fixed
- **A heavy task waits while the whole server is short of memory.** LoadGuard only watched Jellyfin's own memory, so a scan could start while another program, such as an encoder or a download client, was using most of the server's. Now, on Linux, a heavy task that starts while the whole server is past the memory ceiling waits until memory is back below it, or two hours at most; work already under way is left to finish. ZFS's cache (ARC) above its minimum counts as free memory here and in the server's load profile, as the system gives it back when programs need it.

## [1.0.20] – 2026-10-15

### Added
- **Schedule lists the tasks that have no time set.** Below the timeline, each task that never runs on its own (or only when Jellyfin starts) is listed with what it does and whether Medic recommends turning it on, and how often. Tasks Medic knows are recommended on, Scheduled restart is recommended off unless the server slows down the longer it runs, and tasks Medic doesn't know are left to you.
- **The schedule plan avoids times when the whole server is busy, not only when people watch.** On Linux, Medic now notes every 5 minutes how busy the machine is as a whole (CPU, memory, and the busiest drive) and keeps it as averages by hour of the week. After 3 days of readings the planner steers tasks away from hours when the server is usually busy, such as when downloads or backups run, and warns when a task can only go in one. It reads the totals Linux shows every program and never looks at what is using them, so Medic doesn't learn which program, container or app makes the server busy. Readings taken while a Jellyfin scheduled task runs are left out, so a task isn't pushed away from its own time. Nothing is read on Windows or macOS.
- **Groundwork for a fuller Dashboard.** Medic now puts side by side, for the Dashboard panels to come, what it already records: busy hours next to the week's task runs, today's planned runs next to the ones that ran, why files are transcoded next to what in the library causes it, each drive's last 30 days, your connection next to the most people watching at once, repeated log errors, accounts and updates. Nothing new is collected or sent anywhere; the daily disk reading now also notes what each drive holds.

### Fixed
- **On Windows and macOS, a drive keeps one name as it fills.** Where Medic can't read mount points, it named a drive by its size and free space, so the name changed whenever the free space did, and a drive rarely kept one name for the 3 days of readings that its fill rate and the "will be full" warning need. It's now named by the volume that holds it. A "will be full" warning you ignored on Windows or macOS needs ignoring once more.
- **Ignoring a repeated error in the log keeps working when it repeats again.** Its title says how many times the error has repeated, so it came back after being ignored, and two different errors with the same count shared one ignore. It's now named by the error itself. A repeated error you ignored before needs ignoring once more.
- **Two drives that hold the same library each keep their own "will be full" warning.** When one library is spread over two drives that hold nothing else, both warnings had the same name, so ignoring one also hid the other. The name now uses the drive's mount point when Medic can read it. A "will be full" warning you ignored in 1.0.19 needs ignoring once more.
- **Memory on "Right now" shows its real share.** It always said 0%, so its warning colours never showed. Medic now sends the share with the reading.
- **The Dashboard fills the width of the screen.** On a wide screen it left whole columns empty on the right. Tiles now sit in lines that always reach the right edge, two to a line on medium screens and one on phones, where Health, Right now and Top issues come first. Long names without spaces, such as a file path in a task's error, wrap inside their tile.
- **"Last 24 hours" counts every run.** With "Show errors only" ticked on the History tab, the tile counted only the errors. It now has its own list, and says how many runs completed, failed, stopped or were cancelled; a stopped run is no longer called "failed".
- **An old LoadGuard stop no longer shows as a warning.** The Dashboard showed the latest LoadGuard event however old it was. It now shows only the last day, and a stop stays a warning only until that task restarts.
- **"Today's tasks" stays current.** "Next at" and the line for now stayed where they were when the schedule was first loaded. They now move with the clock, and the schedule is fetched again every 15 minutes and when the day changes.
- **Details opens Checks at the issue.** It opened Checks at the top of the list. It now scrolls to the issue, focuses it and marks it.
- **Ignoring an issue doesn't count it as fixed.** "This week" counted an issue you ignored as fixed, because ignored issues were left out of each day's record. Medic now records them, so "This week" says how many you ignored, and an issue you show again isn't counted as new.

## [1.0.19] – 2026-10-14

### Fixed
- **An ignored finding stays ignored when a number in it changes.** Medic remembered an ignored finding by its title, and some titles include a count, a time or a percentage, such as "Jellyfin ran flat out 3 times this week", "2 plugin updates are available" or "The drive for Movies will be full in 3 weeks". When the number changed, the finding came back, and the weekly summary could list it as both new and fixed. These findings now have a fixed name that ignoring and the weekly summary use instead. Findings like these that you ignored before need ignoring once more.

Thanks @camarigor (#111, #112, #114, #115) for the clean-up in this release: the unused code removed and the test Seerr kept off the network came in through #113.

## [1.0.18] – 2026-10-14

### Fixed
- **Track cleanup no longer removes the wrong track when a file has changed.** It worked out which tracks to keep from Jellyfin's list of the file's tracks, which can be out of date: after an earlier cleanup, or when Sonarr or Radarr replaced the file and Jellyfin hadn't read it again. It then kept tracks by their old numbers, so it could drop a language you keep (with "replace in place" on, for good). Each file is now checked with FFprobe right before it's changed; if it doesn't match, it's skipped and Jellyfin is asked to read it again, and Jellyfin is also asked to read every file Medic changes.
- **Track cleanup checks the new copy properly before it replaces anything.** It only checked that FFmpeg finished and that the new file was at least a third of the original's size. A file that can't be read to the end makes FFmpeg stop early and still report success, and the shorter copy passed that check; with "replace in place" on, the original was then deleted. The new copy must now have the same video and attachments, exactly the audio and subtitle tracks that were kept, and the same length as the original, and FFmpeg's own error is shown when it fails. A remux that writes nothing for 10 minutes (a hung network drive) is stopped.
- **No moment without a file.** "Replace in place" now swaps in one step. If Jellyfin stops halfway through the default swap (original kept aside, new copy not yet in place), the next run or "Tidy copies" puts the original back instead of deleting the leftover copy, and tidying copies from Medic 1.0.4 to 1.0.7 no longer deletes the original before its replacement is in place.
- **Track cleanup no longer makes hardlinked files use more space, and keeps owners.** A file with more than one hard link (usually because a torrent client seeds it) is now skipped, as cleaning it would leave the shared file in place and add a second one; the weekly report counted that as space saved. The new copy gets the original's permissions and, where Jellyfin may, its owner, so Sonarr or Radarr running as another user can still replace it. A file is also skipped when its drive hasn't room for the new copy, or when Sonarr or Radarr replaces it while it's being cleaned (the newer file is kept).
- **The support export no longer lets some secrets through.** In lists of names and values, such as custom HTTP headers, the secret is in the value under a harmless name (`<Key>Authorization</Key><Value>Bearer ...</Value>`), and the export hid the name and showed the value. Such values (also when written as `name`/`value` attributes) are now hidden when their name looks secret, and the name is shown. Credentials after Bearer, Basic or Digest, web tokens (JWT), API keys written as 32 characters without dashes, and settings named like cookies or ending in "key" (ServerKey, AuthKey) are hidden too. The same patterns now also hide these in log lines Medic shows.
- **Scheduling works on servers in other languages.** Medic recognised Jellyfin's tasks by their names, which Jellyfin translates into the server's language: on a server in Portuguese it planned 1 task out of 12 and left the rest as "Medic doesn't know what this task needs". Jellyfin's own tasks are now recognised by their key, which never changes; tasks from plugins are still recognised by name.
- **The report and the memory guard recognise tasks in any language too.** The library scan and database checks, the "heavy tasks during the day" check and the memory guard's list of heavy tasks also went by translated names, so on non-English servers those checks found nothing and the guard never stepped in. They now use the same keys.
- **The schedule planner sees tasks that run past midnight.** Its day stopped at midnight, so a task you keep at 23:30 that takes an hour only blocked 23:30 to midnight, and a planned task could be put at 00:00 on top of it. A planned task also had to end by midnight, so it couldn't start at, say, 23:00 and run on into a quiet night. The planner now plans on the whole week, and a run carries on into the next day (Saturday night into Sunday). A kept task that Medic runs once a month now keeps that time free too, and a kept task that starts between quarter hours (23:50, say) blocks out all of its run.
- **A schedule you change during a back-off is kept.** When a task fails, Medic moves it 3 hours later and puts the old time back after its next successful run. It put the old time back even when you had changed the schedule in Jellyfin's dashboard in the meantime. Now your change stays and the back-off simply ends.
- **A schedule change can no longer leave a task with no schedule at all.** Applying a schedule cleared a monthly task's Jellyfin triggers before saving Medic's own note of when to run it, so if that save failed (a full disk, say) the task never ran again. Medic now saves its note first, and if that fails nothing is changed and the page says so. Restoring a backup works the same way.
- **A monthly run that starts during an apply or restore is no longer forgotten.** Both wrote Medic's monthly schedule back from an earlier read, so a run started in between could be lost, and the task run again.
- **A damaged Medic file is kept instead of overwritten.** When one of Medic's files couldn't be read, the next change replaced it with a new one, losing what was in it. It's now set aside with `.bad` added to its name, and the Jellyfin log says so. Files are also flushed to disk before they replace the old ones.
- **Two schedule backups made in the same second no longer share a name.** The second replaced the first, so a restore straight after an apply could lose the schedule from before the apply.
- **Restarts wait for Live TV recordings and for the gap between episodes.** Medic's restart tasks only looked for something playing, track cleanup and other scheduled tasks, so a restart could cut a Live TV recording short, or land in the few seconds between two episodes when nothing shows as playing. They now also wait while a recording is in progress, and a quiet moment has to still be quiet two minutes later.

### Changed
- **"Restart Jellyfin for waiting updates" no longer has a time set when Medic is installed.** It restarted Jellyfin at 04:00 by default whenever plugin updates were waiting, without anyone choosing that. Like Scheduled restart, it now does nothing until you give it a time under Scheduled Tasks. If you never changed its time, it has none after this update: add one to keep updates finishing overnight. A time you set yourself stays.
- **Medic keeps the 50 newest schedule backups and the newest 1000 schedule changes.** Both grew without limit.
- **Data from Task Advisor and Setup Optimiser is brought over only once.** Medic copied any of their files missing from its own folder on every start, so a file you deleted, say to reset ignored findings, came back after a restart while the old folder was still there. It now copies them on its first start only.
- **SSDs and NVMe drives on Unraid get their own temperature limit, and heat is no longer reported as a failing disk.** A drive at 55°C or more was listed under "A disk may be failing", NVMe drives included, which commonly run warmer than that. Heat now has its own finding, "A disk is running hot", from 55°C for spinning disks and 70°C for SSDs and NVMe drives.
- **A plugin repository that's down no longer slows every report.** Medic only remembered addresses that answered, so one that didn't was tried again, twice, each time the report opened, which could add about 14 seconds. A failure is now remembered for 2 minutes.
- **A hung nvidia-smi is no longer started again every 15 seconds.** When nvidia-smi took more than 3 seconds or answered with something that isn't a number, Medic tried again at the next reading; it now waits 5 minutes. Finding the processes Jellyfin started also goes through the process list once, not once for each process found.
- **A problem reading this machine's hardware no longer takes the whole report down.** If Medic couldn't read the hardware or list the specs, the report failed with an error; now everything else still comes back, with a note under Hardware. Checks that couldn't run are also written to the Jellyfin log with the reason, and so is a recommended schedule that couldn't be worked out.
- **The exposure check says where it tested from.** For a domain, or an address over http, the check connects from this server itself, but the result said "from the internet". It now says when a result is only this server's view, and a domain that doesn't answer from here is no longer shown in green as closed: a router that can't reach its own public address from inside, or a domain that points inside your network, looked the same. A certificate this server doesn't trust now counts as something answering, redirects are no longer followed, and an address with a path in it, or an IPv6 address over http, no longer breaks the check.
- **Your public IP is looked up only when you press "Use my IP + 8096".** Opening the Dashboard asked a service on the internet for it every time. It's now asked for on that button only, and kept for an hour.
- **"Signs in from several places" counts per account.** Sign-ins were grouped by the activity entry's text, which is translated, names the device, and above 40 characters was replaced by "a user", so different people with long names were added up into one "account" with many addresses. They're now grouped by account, and an IPv6 address counts by its /64 network, as phones change IPv6 address often. When most failed sign-ins come from a private address, the advice now says it's probably your reverse proxy and to add it under Known proxies, and mentions Jellyfin's own limit on failed sign-ins per user.
- **No waiting on the Dashboard, and a sign-in check that says when it couldn't run.** Several Dashboards asking for the current figures within a second each waited half a second in turn for a CPU reading; they now share one. The 5-minute CPU line also measured only since the Dashboard's last poll, and now measures its own 5 minutes. When the activity log doesn't answer in time, the security check now says sign-ins couldn't be checked, rather than looking like a week with no failed sign-ins.
- **Less work on every request.** Each request to Medic searched every loaded assembly for the activity log's type, including the Dashboard's frequent refreshes. It's now found once.
- **Plugin advice no longer says "remove it" when Medic couldn't count.** A count that failed, or asked about a kind of item this Jellyfin doesn't have, came back as 0, so advice such as "there are no books on this server, remove the book provider" could rest on a number nobody knew. Such counts are now unknown, and advice that depends on them is left out. The Intro Skipper advice also recommends the same limit it warns about (it warned above half the CPU threads but suggested a quarter).
- **The memory guard measures Jellyfin, not the whole machine.** It read memory use from the top of the cgroup tree, which inside a container is the container but on a server without one (or with the older cgroup v1) is the whole machine, so other programs' memory could make it stop Jellyfin's tasks. It now reads Jellyfin's own cgroup. A task it had paused was also forgotten if it wasn't quite stopped when the guard came back to it; it now waits its turn instead.
- **Software RAID is no longer taken for Unraid, and Podman is recognised.** Any `/dev/mdN` device in the container's mounts made Medic think it ran on Unraid, which plain Linux software RAID also has, so those servers got Unraid instructions. Unraid is now recognised by its user shares. Podman containers were treated as plain Linux and are now given the container advice. The two places that worked out whether Jellyfin runs in a container now use the same check.
- **The Medic page stops checking the server when you're not looking at it.** While a track scan or run was going, the page kept asking for its progress every few seconds after you went to another Jellyfin page, and the Dashboard's live tiles kept refreshing in a browser tab in the background. Clicking between tabs during a run could also start the same check twice over. All of these now stop when you leave the page or hide its tab, start again when you come back, and never run twice at once.
- **Home page banners stay with the admin they're for.** The banners were kept for five minutes without noting whose they were, and Jellyfin's web app doesn't reload when someone signs out and another person signs in, so the next person could see an admin's alerts (which can include a log line with paths). They're now only reused for the same person and cleared as soon as someone else signs in, and banners dismissed in this browser are remembered per person.
- **The speed test measures slow uploads, and no longer advises 1 Mbps on a failed measurement.** Upload was only counted in whole 2 MB pieces finished within its 8 seconds, so below roughly 8 Mbps nothing finished, the result was 0 Mbps, and the advice was a 1 Mbps streaming limit. Upload is now counted as it's sent, and when nothing could be measured no limit is suggested.
- **Cleanup no longer fails on a folder it can't read.** Finding leftover transcode files, logs or IPTV `.strm` files walked folders lazily, so a subfolder Jellyfin isn't allowed to read threw part way: the cleanup check failed with an error and cleaning stopped halfway. Such folders are now skipped.
- **Off and Suggest no longer clear the Schedule's time range.** The time range buttons' handler also caught the Off and Suggest buttons in Settings, so choosing one cleared the range, and choosing a range unmarked Off and Suggest.
- **The arrow keys reach the Media report tab.** The tab list they used left it out, so they skipped it.
- **"What's new" behaves like a dialog.** It now takes the focus when it opens, closes with Escape, keeps Tab inside it, and gives the focus back when it closes. Each tab also says which panel it controls, for screen readers.
- **The playback test no longer puts your access token in the address.** It sent the token as `api_key` in every playlist and segment address, which ends up in proxy and server logs; it now goes in a header.

## [1.0.17] – 2026-10-11

### Added
- **Contact tab.** Report a problem on GitHub (the new issue is already titled for Jellyfin Medic), see the open issues, or email for anything private.

### Changed
- **New contact email.** Plugin settings shared from the Plugins tab now go to p1zzm1ssilerepo@gmail.com.

## [1.0.16] – 2026-10-10

### Fixed
- **No more "audio normalisation scanning is on" for films, series and IPTV libraries.** Jellyfin only measures loudness for music and only shows that option on music libraries, so elsewhere it couldn't be switched off and the advice couldn't be followed. Medic now only mentions it for large music libraries.
- **"What's new" now shows after an update.** It only checked the first time Medic's page was opened after the web app loaded, and Jellyfin's web app doesn't reload after a restart, so going back into Medic after an update never showed it. It now checks every time you open Medic.
- **A "what's new" banner on the home page** for admins after Medic updates, with the headline of each change. Closing it, or seeing "What's new" in Medic, hides it on every device until the next update.

## [1.0.15] – 2026-10-10

### Fixed
- **1.0.15 is 1.0.14 as intended.** The 1.0.14 download was built from the code before its changes, so it still showed the false "database looks damaged" and "database upgrade failed" banners. 1.0.15 has everything listed under 1.0.14 below.

## [1.0.14] – 2026-10-10

### Fixed
- **False "database damaged" and "database upgrade failed" banners.** The home-page banners reacted to warnings that only mention those words, such as Jellyfin's "This may indicate a removed plugin or database corruption" (a leftover item from a removed plugin) and Entity Framework's routine note that a migration step "cannot be executed in a transaction". They now only react to errors and fatal errors, and the exception text printed under them.
- **The banners' problems now show in Checks too**, under Logs, so the two always agree.

### Changed
- **Banners and "What's new" are one-time, on every device.** Dismissing a banner, or seeing "What's new" after an update, is now remembered on the server for your account, so your other phones, browsers and TVs don't show it again. A banner only comes back if the problem happens again on another day.

### Added
- **What's new in each version.** A button in Medic's Settings shows every version's changes.

## [1.0.13] – 2026-10-09

### Added
- **Resource monitoring for Jellyfin itself.** Every 15 seconds Medic measures what Jellyfin and the FFmpeg processes it starts are using (CPU, memory, disk and, for NVIDIA and AMD, the GPU), and nothing else on the machine. The Dashboard shows it under Right now. When one stays near its limit for 45 seconds, Medic records what was running: scheduled tasks, transcodes (with the titles), and other FFmpeg work such as trickplay or chapter images. The Dashboard lists this week's spells, and Checks sums up the most common causes. Intel GPU load can't be read without extra tools, and work a plugin does outside a scheduled task can't be told apart from Jellyfin's own, as all plugins run in Jellyfin's process.
- **Theme checks.** A new Themes section in Checks looks at your custom CSS (Dashboard → General): themes whose address doesn't load, @import lines placed after other rules (browsers ignore them, the usual reason a theme "stops working"), themes loaded from raw.githubusercontent.com (browsers refuse those as stylesheets), themes loaded over plain http, and unbalanced braces or unclosed comments that make the browser drop the rest of the CSS. Medic can't see how a page actually draws, so it can't catch every display problem.
- **Better disk space checks.** Medic now watches every drive your libraries are on, not just the config and cache drives, and warns when one is nearly full. It records each drive's free space once a day and tells you how fast it's filling, with a warning when a drive will be full within a month ("full in about 3 weeks"). Specs also show how much space the metadata folder, trickplay images and image cache take (measured in the background, as they can hold millions of files).
- **Banners for admins on the home page.** Serious problems now show as a banner the moment they appear in Jellyfin's log, however rarely they happen: a damaged database ("database disk image is malformed"), a full disk, a failed database upgrade, a plugin that couldn't load, or a fatal error. A second banner says when Jellyfin needs a restart to finish installing updates. Only admins see them, each can be dismissed (it comes back if the problem happens again on another day), and both can be turned off in Medic's settings. If the database is so damaged that Jellyfin can't start, no plugin can show anything.
- **Choose per task in Preview.** Each task in the schedule preview now has a choice: let Medic schedule it, keep its current schedule, or leave it unscheduled. Medic remembers your choice. A task with no schedule (such as Scheduled restart) is never given one unless you choose "Let Medic schedule it".
- **Time left for running tasks.** Running tasks on the Dashboard now show roughly how long they have left, for example "Scan Media Library (42%, about 12 min left)". It works from the task's progress so far and, early on, from how long it usually takes.
- **Leave chosen folders, shows or films out of track cleanup.** Under Track settings, "Leave these alone" takes one entry per line: a folder (such as /media/anime) or part of a show's, film's or file's name. Anything matching is never scanned or changed. IPTV and other streamed files were already skipped.

### Changed
- **More performance advice.** Medic now flags Jellyfin's database on a spinning hard drive (it tells real disks from virtual ones, which always claim to spin), a very large database or image cache, image resizing allowed to use every thread on a small CPU, and a very long list of plugins.
- **More precise GPU advice.** With a GPU in use, Medic now also checks that H264 and HEVC are both decoded on it, that 10-bit HEVC (most HDR) is decoded on it, whether transcodes can be made in HEVC for apps that play it (about half the bitrate for remote viewers), Intel's low-power encoders on QuickSync and VAAPI, and hardware encoding for trickplay. Without a GPU, it flags slow software presets and suggests key-frame-only trickplay, which is many times faster. Medic can't switch the GPU on for you; each finding says which setting to change.
- **IPTV lists every duplicate channel.** Instead of just saying some live channels appear several times, the IPTV tab now lists each channel with copies and the name of every copy (for example "BBC One · BBC One HD · UK: BBC One FHD"), with a filter box, so you can see which to keep. The Dispatcharr advice is gone.

### Fixed
- **"Suggest" schedule mode works.** The check that tells you a better schedule is available ("A better schedule is available" in Checks) was never run, so Suggest mode did nothing. It now runs with every check.
- **The schedule uses 15-minute slots properly.** Medic planned in 15-minute blocks, but judged how busy each one was by the hour, so a block at 02:00 looked no better than one at 02:15 and tasks kept landing on the hour. It now knows how busy each quarter hour is: straight away from a smooth curve through your hourly viewing, then more exactly as it records each quarter hour (it starts doing this now). Press Preview, then Apply on the Schedule tab to use it.
- **Quick tasks take one 15-minute slot.** Every task used to hold at least 30 minutes, even one that finishes in a minute, so tasks stepped along every half hour or hour. A quick task now takes a single slot with at least 5 minutes spare (longer tasks still get a quarter of their run time spare), so the night's tasks run back to back and finish sooner.
- **The "Never schedule tasks between" window goes in 15-minute steps**, for example 18:30 to 22:45. Your existing hours carry over.
- **The Schedule shading is drawn per quarter hour**, to match what the planner uses.
- **"Tasks run while people are usually watching" shows each task's real time**, such as 04:15, instead of rounding it down to 04:00.

## [1.0.12] – 2026-10-06

### Fixed
- **The IPTV clean-up could delete your whole IPTV library.** The Dashboard's "Clear IPTV stream files" removed every stream file in your Xtream Library folder, not just the left-over ones. It now only removes stream files Jellyfin no longer has an item for, never touches files less than 2 days old (a new sync may not be scanned yet), and offers nothing if it can't tell which are left over. If you used it before, a re-sync in Xtream Library brings your channels back.
- **Track cleanup could remove a language you keep.** French, German, Dutch and Chinese each have two 3-letter codes (for example "fre" and "fra"). Keeping one now keeps tracks tagged with either.
- **Stop now really stops track cleanup.** FFmpeg used to carry on in the background until it finished the file.
- **A film can no longer go missing during track cleanup.** If putting the cleaned file in place fails, the original is put back.
- **Styled subtitles keep their fonts.** Track cleanup now keeps the fonts stored inside MKV files, which styled subtitles (common in anime) need.
- **Tasks run in a sensible order.** Each night now runs the library scan first, then the tasks that use it (such as the guide, chapter images and trickplay), then upkeep, with monthly jobs last. Before, a task such as Refresh People could land before the scan. The schedule list also reads in the order the night runs. Press Preview, then Apply on the Schedule tab to use it.
- **Fewer tasks running at once.** Each task gets enough time for its longest recent run, with more room for long tasks, and runs that failed early no longer make a task look quick. This stops tasks running into each other.
- **The memory guard stops the right task.** When memory runs high, it now keeps the task that started first, as intended, instead of sometimes stopping a library scan that was nearly done.
- **Failed sign-in warnings work again.** On Jellyfin 10.9 and later, the warnings about many failed sign-ins and accounts signing in from several places never appeared.
- **Webhook links are hidden in the settings export.** Discord, Slack and similar webhook links carry their secret inside the link, and are now shown as ****.
- **Medic's saved data survives a crash.** Schedules, run history, monthly tasks, your viewing pattern and other saved data are now written safely, so a crash or power cut mid-save can't wipe them. Losing the monthly task list used to mean those tasks quietly stopped running.

## [1.0.11] – 2026-10-05

Includes everything in 1.0.10, which wasn't released on its own.

### Fixed
- **Languages you set by hand stay on the right track.** After stripping tracks, a language you'd set could be applied to a different track the next time, because the track numbers change. Medic now forgets them once they're written into the file, and won't let you set languages from a scan that's out of date.
- Renaming a separate subtitle file keeps flags such as "forced" or "sdh" in its name, so "Film.forced.srt" becomes "Film.eng.forced.srt".
- Languages are checked when set one track at a time, and 2-letter codes are saved in the 3-letter form files use ("en" becomes "eng").
- **"This week" counts freed space correctly.** With kept originals, space from track cleanup is counted when the originals are deleted, not twice.
- Repeated-error counts no longer include yesterday's errors from a log file that started before midnight.
- Hand-set languages and the "This week" history are saved safely, so a crash can't wipe them.

## [1.0.10] – 2026-10-05

### Added
- **See a running scan or track run straight away.** If a track scan or a track cleanup run is going when you open Medic, a bar at the top of every tab shows how far it's got, with a button to the Tracks tab. No need to scan again.
- **Set the language for lots of tracks at once.** Set everything matching the list's filter, or just the page you're on. By default audio is only labelled where it's the film's only audio track, and anime is skipped, because its untagged audio is often Japanese.
- **This week.** A new Dashboard tile shows how many issues were fixed or appeared in the last week, how much space Medic freed (track cleanup, clean-ups, duplicates), and how often it restarted Jellyfin.
- **Set a track's language yourself.** The Tracks tab lists every track with no language, film by film. Pick the language and Medic does the rest: tracks inside a film are labelled, with a readable title such as "English 5.1", the next time you strip tracks, and separate subtitle files are renamed straight away to the form Jellyfin recognises, such as "Film (2009).eng.srt".
- **Pages and search** for "Tracks with no language set" and "What would change", so you can go through every film, not just the first 50.
- **Red, yellow and green markers on Jellyfin's settings.** On the Settings tab, each Jellyfin setting is marked: red needs changing, yellow could be better, green is fine. Each has an Open link to its page in Jellyfin, which shows the issue box, and Dismiss turns it green. Tick "Only settings with advice" to see just those. Settings also have plain names now.
- **Clear old logs.** The Dashboard's clean-up panel can remove Jellyfin log files older than a day.
- **"Fixed it: count again from now"** on repeated-error findings, so the count starts again from zero once you've fixed the cause. These findings also say when the error last happened.

### Changed
- The no-language list shows one track per row with the film, type, format, file size and track size, instead of library-wide totals that were easy to misread.
- Plainer wording on the Tracks tab and in the run log.

### Fixed
- Coming back to Medic during a track run no longer hides its progress until you scan again. The progress shows straight away, and the last scan's lists stay visible, marked as out of date.
- Separate subtitle files, such as a .srt beside the film, were treated as tracks inside the film. Medic could keep "removing" a track that wasn't there, and rewrite the same film on every run. Separate files are now left alone, and listed so you can rename them.

## [1.0.9] – 2026-10-04

### Added
- **Restarts for updates, handled for you.** A new scheduled task, "Restart Jellyfin for waiting updates", restarts Jellyfin when plugin updates are waiting, but only when nobody's watching, no track cleanup is running and no other scheduled task is busy. If it isn't safe, it checks again every 5 minutes for up to an hour, then waits for its next run. It runs at 04:00 by default; change the time under Scheduled Tasks. Checks also tells you when a restart is waiting.
- **Scheduled restart.** A second task, "Scheduled restart", restarts Jellyfin at whatever times you give it, for example once a week to clear memory, with the same safety checks. It has no time set to begin with, so it never restarts your server until you add one.
- **Pause, resume and a time window for track cleanup.** Pause finishes the file it's on, then waits until you press Resume. You can also limit track cleanup to set hours, such as 01:00 to 07:00: start a run at any time and it waits for the window, works through it, and carries on in the next one. By default it also holds off while anyone is watching. Time spent waiting is left out of the time-left estimate.
- **The issue, on the page where you fix it.** When you follow a "Where" link, the Jellyfin page it opens shows the issue in a small box: what's set now, what Medic suggests, and why. You can drag it anywhere, minimise it or close it. It's kept in your browser only, and disappears when you close it or after 30 minutes.

### Fixed
- Links for plugin issues now open that plugin's own settings page, instead of the list of all plugins.

## [1.0.8] – 2026-10-04

### Changed
- **One file per title.** Track cleanup no longer leaves a stripped copy beside the original. The cleaned file takes the original's place under the same name, so Jellyfin keeps the same item, artwork and watched status. The original is kept in a hidden .medic-originals folder next to it, which Jellyfin, Sonarr and Radarr ignore, until you choose "Delete the kept originals".

### Added
- **Remove duplicate files.** A button on the Tracks tab puts every title left with several copies by earlier versions back to a single file under its original name, and deletes the copies of copies. Tick "Delete the originals too" to keep only the cleaned file and free the most space.
- **Kept originals at a glance.** The Tracks tab shows how many originals Medic is keeping and how much space they take, with a button to delete them once you're happy.

### Fixed
- Track cleanup could pick up its own stripped copies and strip them again, leaving files like "Film.medic-stripped.medic-stripped.mkv". It now never touches its own files.

## [1.0.7] – 2026-10-04

### Fixed
- **Track scans carry on when you leave the tab.** Scan and preview now runs on the server, with a "Scanning x of y files" progress line. Switching tabs, or leaving Medic and coming back, shows the scan's live progress or its last result, so there's no need to scan again. The last result is cleared when you change track settings or strip tracks, because it would be out of date.

## [1.0.6] – 2026-10-04

### Added
- **Media report.** A new read-only tab shows what formats your files are in, roughly how much space converting them to HEVC could save (with a GPU encoder and with software encoding), and which files are likely to make Jellyfin transcode, such as DTS-only audio or picture-based subtitles. It reads what Jellyfin already knows about each file, so nothing is rescanned and nothing is changed.
- **Files that get transcoded.** Medic's 5-minute playback check now also notes which files were being transcoded and why, and the Media report lists them. It stores titles and reasons only, not who was watching.
- **Links to the fix.** The "Where" tips on Checks and on plugin suggestions are now links. Jellyfin settings pages open in a new tab, and links to Medic's own tabs open in place.
- **Advice for your platform.** Medic now works out where Jellyfin is running (Unraid, TrueNAS SCALE, Proxmox, Docker, Linux, Windows or macOS) and words its advice and "Where" directions for that setup, from passing the GPU through to moving Jellyfin's data. Unraid-only advice now only appears on Unraid, and the GPU device checks, which can only see devices on Linux, no longer misfire on Windows or macOS.
- **Time left for track cleanup.** While tracks are being removed, the Tracks tab shows how much of the data is done, roughly how long is left, and about when it will finish. It's worked out from file sizes, so a large 4K file counts for more than a small DVD rip.

### Changed
- **Track settings are now on the Tracks tab**, next to the tool they control, with their own Save button.

### Fixed
- TMDb, OMDb, Fanart, TheTVDB and similar plugins are no longer reported as "not set up" when their API key is blank. They come with a built-in key, so blank is normal.
- The "downloads full-size original images" tip for TMDb now counts only films and TV, and only appears for large film and TV libraries. Music was being counted before.
- Real-time monitoring advice no longer suggests Sonarr or Radarr for music libraries. It names Sonarr for TV, Radarr for films, and a daily scan for everything else.
- Plugin findings now name the plugin in "Where", instead of "(plugin)".

## [1.0.5] – 2026-10-04

### Added
- **Plugin suggestions.** The Plugin directory tab now opens with plugins suggested for your server, based on what's in your libraries, what your users actually watch and the plugins you already have.
- Suggests plugins worth adding, such as Intro Skipper when most viewing is TV, an anime metadata provider when anime is found, or lyrics for a music library.
- Flags plugins you probably don't need, such as an anime provider with no anime on the server, or Bookshelf, which Jellyfin 12 has replaced.
- Each suggestion explains why, says how important it is, and shows whether it's in your plugin catalogue. Nothing is installed or removed for you.
- **Untagged subtitles.** A new Track cleanup setting removes subtitle tracks with no language tag, often a whole disc's worth of foreign subtitles, while always keeping untagged audio, which is often a film's main soundtrack.
- It keeps the first untagged subtitle in films that have no subtitle in your languages, since that's usually the film's own language. An optional setting also removes a film's only subtitle when it's untagged.
- Films whose audio is tagged as a language you don't keep always keep their subtitles, so foreign films and anime stay watchable.

### Changed
- The old "allow removing undetermined tracks" setting is now labelled "Remove all untagged tracks, audio included", to make clear it can remove a film's soundtrack.

### Fixed
- The Undetermined tracks list counted a file once for every untagged track in it, so the file counts were too high and the same file repeated in the examples. Files and tracks are now counted separately.

## [1.0.4] – 2026-10-03

### Added
- **Track cleanup (Tracks tab).** Removes unwanted audio and subtitle tracks from local
  files by remuxing (no re-encode, no quality loss). Keeps your languages, forced
  subtitles, and the native track where it's the only one, so anime and foreign films
  stay watchable. Scans and previews first, keeps the original beside a stripped copy by
  default, groups undetermined tracks for review, and has concurrency and thread options.
  Skips IPTV.

### Fixed
- The Tracks endpoints are now correctly registered, so the scan works.

## [1.0.3] – 2026-10-03

### Added
- **Plugin directory.** A new tab lists community Jellyfin plugins — what they do and
  where to find them — fetched live from the awesome-jellyfin project and shown with
  thanks to them.
- **"What's new" panel.** After updating, Medic shows a short summary of what changed in
  the new version, once.
- **Scheduling help modes.** A new setting: "Off" (you run Preview/Apply yourself)
  or "Suggest" (Medic flags a clearly better schedule in Checks and waits for you to
  Apply). Suggest is the default. Medic always backs up and logs before any change.
- **Help on every setting.** A "?" button beside each setting explains what it does
  and why.
- **Memory load guard.** If memory climbs past a ceiling (85% by default) while more
  than one heavy task is running, Medic stops the extra heavy tasks to keep the server
  up, then restarts them one at a time once memory recovers. On/off and the ceiling are
  in Settings. The Dashboard shows memory as a percentage and reports any guard action.
- **Safe IPTV clean-up.** Finds and removes leftover IPTV stream (.strm) files for
  deselected categories, and flags a very large Live TV channel count with the supported
  ways to clear orphaned channels. Medic never edits Jellyfin's database.

## [1.0.2] – 2026-10-02

### Changed
- The exposure check now takes a domain or IP, port and protocol (pre-filled with what
  Medic found), with a shortcut to test the raw 8096 port.
- A plugin repository is only reported as not responding after it fails twice in a row,
  so a momentary blip is no longer called "dead".
- Long error messages on finding cards can now be expanded and scrolled in full.
- Clearer wording when a plugin simply stores no settings to check.

## [1.0.1] – 2026-10-02

### Fixed
- The exposure check could wrongly report a server as unreachable; it now finds the
  public IP and tests reliably.
- Blank connection settings are only flagged when a plugin has none filled in, so
  optional features (such as Xtream Library's Dispatcharr address) aren't reported.
- Works with the way Jellyfin 12.1 provides the user list, fixing a build error.

## [1.0.0] – 2026-10-02

First release. Jellyfin Medic merges the earlier Task Advisor and Setup Optimiser
plugins into one, and adds a new dashboard, a timeline schedule and several new checks.

### Added
- **Dashboard** with health, today's tasks, live server status, top issues, last 24
  hours, users, updates and a maintenance panel.
- **Schedule** as a week timeline that places maintenance tasks in your quiet hours,
  learned from your own viewing pattern, with preview, apply, backup and restore.
- **Checks** covering hardware and transcoding, storage and disk health, server and
  library settings, Live TV, network, scheduled tasks, users and security, plugins and
  the log, each with an Ignore option.
- **Tests**: playback (GPU transcoding), image extraction (GPU vs CPU) and server
  internet speed.
- **IPTV** analysis: genre and country breakdowns, how much gets watched, and duplicate
  channels.
- **Users and security**: inactive and unprotected accounts, failed-login bursts,
  logins from several places, and over-broad library access.
- **Settings for support**: a masked copy of plugin settings, with an optional anonymous
  system summary.
- On first start, migrates data from Task Advisor and Setup Optimiser.

[1.0.10]: https://github.com/p1zzm1ssile/Jellyfin-Medic/releases/tag/v1.0.10
[1.0.9]: https://github.com/p1zzm1ssile/Jellyfin-Medic/releases/tag/v1.0.9
[1.0.8]: https://github.com/p1zzm1ssile/Jellyfin-Medic/releases/tag/v1.0.8
[1.0.7]: https://github.com/p1zzm1ssile/Jellyfin-Medic/releases/tag/v1.0.7
[1.0.6]: https://github.com/p1zzm1ssile/Jellyfin-Medic/releases/tag/v1.0.6
[1.0.5]: https://github.com/p1zzm1ssile/Jellyfin-Medic/releases/tag/v1.0.5
[1.0.4]: https://github.com/p1zzm1ssile/Jellyfin-Medic/releases/tag/v1.0.4
[1.0.3]: https://github.com/p1zzm1ssile/Jellyfin-Medic/releases/tag/v1.0.3
[1.0.2]: https://github.com/p1zzm1ssile/Jellyfin-Medic/releases/tag/v1.0.2
[1.0.1]: https://github.com/p1zzm1ssile/Jellyfin-Medic/releases/tag/v1.0.1
[1.0.0]: https://github.com/p1zzm1ssile/Jellyfin-Medic/releases/tag/v1.0.0
