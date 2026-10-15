# Library Cover Maker

A small web page for making animated library covers for Jellyfin. Add up to 5 videos or pictures, type the library name, and it builds an animated WebP that cuts between random moments from each file, with a rounded white frame and the name in a big serif font.

Everything runs in your browser. Your videos are never uploaded anywhere, not even to the container.

## Run it

```sh
cd tools/cover-maker
docker compose up -d --build
```

Then open `http://<your-server-ip>:8097` in Edge, Chrome or Firefox.

Without Compose:

```sh
docker build -t jellyfin-cover-maker tools/cover-maker
docker run -d --name jellyfin-cover-maker -p 8097:80 --restart unless-stopped jellyfin-cover-maker
```

To use a different port, change `8097` to any free port. Jellyfin itself uses 8096.

The fonts load from Google Fonts, so the computer you open the page on needs internet access. Without it the text falls back to Georgia.

## Use the cover in Jellyfin

1. Click **Download animated cover**.
2. In Jellyfin, right-click the library → **Edit images** → upload the file with type **Logo**.
3. Add this to **Dashboard → General → Custom CSS code**, with your library's ID (the `topParentId` in the address bar when you open the library):

```css
.card[data-id="YOUR_LIBRARY_ID"] .cardImageContainer {
  background-image: url("/Items/YOUR_LIBRARY_ID/Images/Logo") !important;
}
```

Leave `maxWidth` and `quality` off the image address: if Jellyfin resizes the image, it can lose its animation.
