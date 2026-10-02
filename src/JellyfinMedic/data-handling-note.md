# Jellyfin Medic: how I handle settings files sent to jellyfinmedic@anglernook.com

A private note to myself, so the promise shown in the plugin ("personal data isn't
wanted; if any reaches me it's removed and not used") is something I actually do.

## The promise shown to users
- Medic masks the personal data it can identify before the file leaves their server.
- Masking can't be guaranteed to catch everything; it's the user's responsibility to read the file before sending.
- Personal data isn't wanted. If any reaches me, it's removed and not used.
- Sending the file is the user's choice; nothing is sent automatically.

## What I do when a file arrives
1. **Read it only to learn the plugins**, how a plugin stores its settings, what its
   fields mean, so I can write or improve Medic's checks for it.
2. **If I spot personal data the masking missed** (a username, IP, key, token,
   password, a credential inside a URL, or anything else that identifies a person or
   their network): I do not use it, and I delete the email straight away, as described
   below. I may note only the plugin and the field name that leaked, never the value,
   so I can improve the masking.
3. **I never add real values from a sent file into Medic, its code, its tests, or any
   public place.** Anything I bake into the plugin is written by me from scratch or is
   obviously non-personal (setting names, option lists, default values).

## Deletion
- Once I've taken what I need (which plugin, which settings exist), or as soon as I'm
  aware a file contains personal data, I delete the email.
- **Deleting means: delete from the inbox, then empty it from the Deleted/Trash folder
  too**, so no copy is left in the mailbox.
- I don't forward these emails, copy them elsewhere, print them, or keep attachments on
  disk. If I saved an attachment to open it, I delete that file as well.
- Target: nothing kept longer than needed to read it; files with personal data deleted
  the moment I notice.

## Mailbox hygiene
- Keep the mailbox itself protected: strong password and 2-factor on the account.
- Don't auto-forward jellyfinmedic@anglernook.com anywhere.
- Review the folder periodically and clear anything lingering, inbox and Deleted alike.

## If someone asks
- I can tell a sender plainly: the file was used only to improve plugin support, no
  personal data was used, and the email (and any copy in Deleted) has been removed.
