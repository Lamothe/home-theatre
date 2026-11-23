# Home Theatre

Home Theatre is the standalone desktop version of Atomic’s Videos app. Browse movie posters and TV series, pick an episode by season, and resume playback where you left off. The app runs on Electron with a local .NET 10 backend that starts and stops with the window.

## Run from source

Install Node.js 24, the .NET 10 SDK, and FFmpeg with FFprobe. In this folder, run:

```sh
npm ci
npm run setup:electron
npm start
```

After the first build, run `npm exec -- electron .` to open the app without rebuilding.

## Library and watch history

Home Theatre reads `~/Videos/Movies` and `~/Videos/Series` by default. It uses the same filenames, season folders, and `.tmdb` artwork cache as Atomic. To use a different library or enable new artwork downloads, create a `settings.local.json` file in the project directory:

```json
{
  "Video": {
    "VideoRoot": "/path/to/Videos",
    "TmdbApiKey": "your-key"
  }
}
```

Home Theatre stores watched flags and resume points in its Electron profile. On first launch with a `history.local.json` file in the project directory, it imports the Atomic `video-watched:` and `video-progress:` entries without overwriting any entries already in the desktop profile. That file is a JSON object whose keys and string values come from Atomic’s browser storage. Both local JSON files are gitignored.
