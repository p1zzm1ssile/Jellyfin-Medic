# Medic Posters changelog

All notable changes to Medic Posters are recorded here. The newest version is at the top.
Jellyfin Medic, Medic Picks and Medic Profiles have their own changelogs.

## [1.2.0] – 2026-10-17

### Added
- **Leave libraries out.** Every library is ticked; untick one, such as an IPTV library, and nothing in it gets a poster and it gets no cover. Libraries added later are included. The libraries chosen in 1.0 and 1.1 are kept: the ones not chosen show unticked.
- **How much space they'll take.** The page says how much the animated posters and covers use now, and for each kind how many are left to make and roughly what they'll come to, and what making them all again would. It works the size out from the settings on screen, so it changes as you change them, and from the ones made so far.
- **Posters and library covers on their own.** **Make posters now** and **Make library covers now**, and **Make all posters again** and **Make all covers again**, each do only their own kind. The nightly task still does both, covers only when **Make library covers each night too** is on.
- **One at a time.** Pick a film or series, or a library, for the preview, then **Use this one** to put that preview on it.

### Changed
- **Library covers have their own settings**: moments, how long each plays, frames per second, colours, the name and the frame. They start at the defaults, so if you changed the poster settings in 1.1, set the cover ones to match.

### Fixed
- **The preview uses the settings on screen.** It used the saved ones, so a change didn't show until it was saved.

## [1.1.0] – 2026-10-17

### Added
- **Every library with videos in it, not only films.** TV, home video, music video and mixed libraries are offered too. A series gets a poster cut from moments of a few of its episodes, and home videos and music videos get one like a film.
- **Animated library covers.** Each library gets a wide 16:9 cover, the shape of Jellyfin's library cards, cut from moments of a few random things in it, with the library's name on it, like the Library Cover Maker's covers. It's a GIF too, so it stays animated on the home screen. Turn it off, or pick its size (320 × 180 up to 640 × 360), under **Library covers**. **Put back** and **Put back the original posters** bring back a library's own picture as well.
- **Preview a library cover** next to **Preview a poster**.

### Changed
- Streams (such as IPTV films added as .strm files) still get no poster: cutting moments from one opens several connections to the provider at once, which many providers don't allow. They're listed with the reason, as before.

## [1.0.0] – 2026-10-17

### Added
- **Animated posters for your films.** Each film gets a short animated poster cut from a few moments of the film (15% to 75% of the way through, past the opening titles and before the credits), with its title in capitals in Bodoni Moda and a rounded white frame, like the Library Cover Maker's library covers.
- **They stay animated in the apps.** Posters are GIFs because Jellyfin never resizes a GIF: other formats are turned into a still picture before an app sees them. They move in the Jellyfin website and Jellyfin Desktop, and in apps that can play a GIF poster.
- **Your own posters are kept.** Nothing in your media folders is changed or deleted. Medic Posters keeps its posters in its own folder and remembers each film's own poster. **Put back** on any poster, or **Put back the original posters** for all of them, brings them back.
- **New films get one each night** from the **Make animated posters** scheduled task (4 am to begin with; Jellyfin Medic can move it into your quiet hours). A poster changed by hand, or by a metadata refresh that replaces images, is left alone. **Make them all again** redoes every film, those included.
- **A preview** of a random film with the current settings, without changing the film, with its file size.
- **Settings** for which film libraries, the size (160 × 240 up to 300 × 450), how many moments and how long each plays, frames per second and the number of colours (all of which change the file size), and whether to draw the title and the frame.
- **HDR films** are converted to normal colour first, so their posters aren't washed out.
- **Films it can't make a poster for** (streams, disc folders, missing files, or files FFmpeg can't read) are listed with the reason.

Thanks @camarigor for the idea of animated posters as a Jellyfin plugin.
