<template>
  <div class="video-page">
    <!-- Player mode -->
    <div
      v-if="mode === 'play'"
      class="player-mode"
    >
      <div
        ref="videoContainer"
        class="video-container"
        @pointermove="showPlayerControls"
        @pointerleave="hidePlayerControls"
        @focusin="showPlayerControls"
      >
        <video
          ref="videoRef"
          class="video"
          controls
          controlslist="nofullscreen"
          playsinline
          @error="onError"
          @loadedmetadata="onLoadedMetadata"
          @ended="onVideoEnded"
          @play="onVideoPlay"
          @pause="onVideoPause"
          @seeking="onVideoSeeking"
          @waiting="onVideoWaiting"
          @playing="onVideoPlaying"
          @canplay="onVideoPlaying"
          @click.self="onVideoClick"
          @dblclick.self="toggleFullscreen"
        >
          <track
            v-if="subtitleTrackUrl"
            :key="subtitleTrackUrl"
            kind="subtitles"
            :label="selectedSubtitleLabel"
            :src="subtitleTrackUrl"
            default
            @load="onSubtitleLoaded"
          />
          Your browser does not support the video tag.
        </video>
        <header
          class="player-toolbar playback-overlay"
          :class="{ 'is-visible': controlsActive || playbackPaused || loading || !!error }"
        >
          <div class="player-heading">
            <button
              type="button"
              class="back-btn"
              aria-label="Back to library"
              title="Back to library"
              @click="exitPlayer"
            >
              <span class="material-symbols-outlined" aria-hidden="true">arrow_back</span>
            </button>
            <h1>{{ currentVideoName }}</h1>
          </div>
          <div class="track-bar">
            <label class="track-field">
              <span class="track-label">Audio</span>
              <select
                v-model="audioChoice"
                :disabled="!probeInfo || starting"
                @change="onTrackChange"
              >
                <option
                  v-for="a in probeInfo?.audio ?? []"
                  :key="a.index"
                  :value="a.index"
                >
                  {{ audioLabel(a) }}
                </option>
              </select>
            </label>
            <label class="track-field">
              <span class="track-label">Subtitles</span>
              <select
                v-model="subtitleChoice"
                :disabled="!probeInfo || starting"
                @change="onTrackChange"
              >
                <option :value="-1">Off</option>
                <option
                  v-for="s in probeInfo?.subtitles ?? []"
                  :key="s.index"
                  :value="s.index"
                >
                  {{ subtitleLabel(s) }}
                </option>
              </select>
            </label>
          </div>
        </header>
        <div
          v-if="playbackReady && !loading && !starting && !error"
          class="playback-actions playback-overlay"
          :class="{ 'is-visible': controlsActive || playbackPaused }"
        >
          <button
            v-if="nextEpisode"
            type="button"
            @click="playNextEpisode"
          >
            Next Episode
            <span class="material-symbols-outlined" aria-hidden="true">skip_next</span>
          </button>
          <button
            type="button"
            class="fullscreen-btn"
            :aria-label="playerFullscreen ? 'Exit fullscreen' : 'Enter fullscreen'"
            :title="playerFullscreen ? 'Exit fullscreen' : 'Enter fullscreen'"
            @pointerdown.prevent
            @click="toggleFullscreen"
          >
            <span class="material-symbols-outlined" aria-hidden="true">
              {{ playerFullscreen ? 'fullscreen_exit' : 'fullscreen' }}
            </span>
          </button>
        </div>
        <div
          v-if="loading"
          class="loading-overlay"
        >
          <div class="spinner" />
          <p>{{ loadingMessage }}</p>
          <p
            v-if="loadingElapsed >= 3"
            class="overlay-detail"
          >
            Still working — {{ loadingElapsed }}s elapsed
          </p>
        </div>
        <div
          v-else-if="buffering"
          class="loading-overlay buffer-overlay"
        >
          <div class="spinner" />
          <p>Preparing video…</p>
          <p class="overlay-detail">{{ bufferDetail }}</p>
          <div class="overlay-progress">
            <div
              class="overlay-progress-fill"
              :style="{ width: bufferPercent + '%' }"
            />
          </div>
        </div>
        <div
          v-if="error"
          class="error-overlay"
        >
          <p class="material-symbols-outlined">error</p>
          <p>{{ error }}</p>
        </div>
      </div>
    </div>

    <!-- Library mode -->
    <main
      v-else
      class="library-mode"
    >
      <header class="library-header">
        <div class="library-brand">
          <img class="library-mark" :src="theatreMark" alt="" width="46" height="46" />
          <div>
            <h1>Home Theatre</h1>
            <p>Movies &amp; TV shows</p>
          </div>
        </div>
        <label class="library-search">
          <span class="material-symbols-outlined" aria-hidden="true">search</span>
          <input v-model="titleSearch" type="search" placeholder="Search titles" aria-label="Search library titles" />
        </label>
      </header>

      <div
        v-if="loadingLibrary"
        class="status"
        role="status"
      >
        Loading library…
      </div>
      <div
        v-else-if="libraryError"
        class="error"
        role="alert"
      >
        {{ libraryError }}
      </div>
      <ul v-else-if="visibleTitles.length" class="poster-grid" aria-label="Movies and TV shows">
        <li v-for="item in visibleTitles" :key="item.encodedPath">
          <button
            type="button"
            class="poster-card"
            @click="openTitle(item)"
            :aria-haspopup="item.type === 'show' ? 'dialog' : undefined"
          >
            <span class="poster-art">
              <span v-if="!item.poster" class="material-symbols-outlined" aria-hidden="true">
                {{ item.type === 'show' ? 'tv' : 'movie' }}
              </span>
              <img
                v-else
                :key="item.poster"
                :src="item.poster"
                alt=""
                loading="lazy"
                class="poster-img"
                @error="failedPosters.add(item.poster)"
              />
            </span>
            <span class="poster-info">
              <span class="poster-title">{{ item.title }}</span>
              <span class="poster-meta">
                {{ item.type === 'movie' ? 'Movie' : seasonLabel(item.seasons.length) }}<template v-if="item.year"> · {{ item.year }}</template>
              </span>
              <span v-if="item.watchStatus" class="poster-watched">{{ item.watchStatus }}</span>
            </span>
          </button>
        </li>
      </ul>
      <p v-else class="empty" role="status">
        {{ libraryTitles.length ? 'No titles match your search.' : 'No videos found.' }}
      </p>

      <dialog
        v-if="activeShow && selectedSeason"
        ref="episodeDialog"
        class="episode-dialog"
        aria-labelledby="show-title"
        @click.self="closeShow"
        @cancel.prevent="closeShow"
      >
        <div class="episode-panel">
          <header class="episode-header">
            <h2 id="show-title">{{ activeShow.title }}</h2>
            <button class="close-btn" type="button" aria-label="Close episodes" autofocus @click="closeShow">
              <span class="material-symbols-outlined" aria-hidden="true">close</span>
            </button>
          </header>
          <div class="season-bar">
            <select v-model="selectedSeason" aria-label="Season">
              <option
                v-for="season in activeShow.seasons"
                :key="seasonKey(season)"
                :value="season"
              >
                {{ season.seasonNumber === 0 ? 'Specials' : `Season ${season.seasonNumber}` }}
              </option>
            </select>
            <span v-if="selectedSeason.episodePaths.length" class="season-progress">
              {{ watchStatus(selectedSeason.episodePaths) }}
            </span>
          </div>
          <p v-if="loadingEpisodes" class="episode-message" role="status">Loading episodes…</p>
          <p v-else-if="episodesError" class="episode-message" role="alert">{{ episodesError }}</p>
          <p v-else-if="!episodes.length" class="episode-message" role="status">No episodes found.</p>
          <div v-else class="episode-list">
            <div v-for="ep in episodes" :key="ep.encodedPath" class="episode-row">
              <button class="episode-item" type="button" :title="ep.fileName" @click="playFromLibrary(ep.encodedPath)">
                <span class="episode-badge">{{ episodeBadge(ep) }}</span>
                <span class="episode-main">
                  <span class="episode-name">{{ episodeLabel(ep) }}</span>
                  <span class="episode-file">{{ ep.fileName }}</span>
                </span>
                <span v-if="isWatched(ep.encodedPath)" class="episode-watched">Watched</span>
                <span class="episode-size">{{ formatSize(ep.size) }}</span>
              </button>
              <button
                type="button"
                class="episode-toggle"
                :aria-label="`Mark ${episodeLabel(ep)} as ${isWatched(ep.encodedPath) ? 'unwatched' : 'watched'}`"
                :title="isWatched(ep.encodedPath) ? 'Mark as unwatched' : 'Mark as watched'"
                @click="setWatched(ep.encodedPath, !isWatched(ep.encodedPath))"
              >
                <span class="material-symbols-outlined" aria-hidden="true">
                  {{ isWatched(ep.encodedPath) ? 'check_circle' : 'radio_button_unchecked' }}
                </span>
              </button>
            </div>
          </div>
        </div>
      </dialog>
    </main>
  </div>
</template>

<script setup lang="ts">
import { ref, computed, nextTick, onMounted, onBeforeUnmount, watch } from "vue";
import { useRoute, useRouter } from "vue-router";
import { apiGet, apiPost } from "../services/api";
import Hls from "hls.js";
import theatreMark from "../../desktop/home-theatre.png";

// --- Types ---
interface LibraryMovie {
  title: string;
  year: number | null;
  encodedPath: string;
  playPath: string;
  posterUrl: string | null;
}
interface LibrarySeason {
  title: string;
  year: number | null;
  seasonNumber: number;
  episodePaths: string[];
  encodedPath: string;
  posterUrl: string | null;
}
interface LibraryShow {
  title: string;
  year: number | null;
  encodedPath: string;
  seasons: LibrarySeason[];
}
type LibraryTitle = (LibraryMovie & { type: "movie" }) | (LibraryShow & { type: "show" });
interface LibraryResponse {
  movies: LibraryMovie[];
  seasons: LibrarySeason[];
}
interface EpisodeInfo {
  season: number | null;
  episode: number | null;
  title: string | null;
  fileName: string;
  size: number;
  encodedPath: string;
}
interface EpisodesResponse {
  episodes: EpisodeInfo[];
}
interface ProbeVideoStream {
  index: number;
  codec: string;
  width: number | null;
  height: number | null;
}
interface ProbeAudioStream {
  index: number;
  codec: string;
  language: string | null;
  title: string | null;
  isDefault: boolean;
}
interface ProbeSubtitleStream {
  index: number;
  codec: string;
  language: string | null;
  title: string | null;
  isDefault: boolean;
  canDeliverAsVtt: boolean;
  isForced?: boolean;
  isHearingImpaired?: boolean;
}
interface ProbeResponse {
  container: string;
  durationSeconds: number;
  video: ProbeVideoStream[];
  audio: ProbeAudioStream[];
  subtitles: ProbeSubtitleStream[];
}
interface StartStreamResponse {
  sessionId: string;
  playlistUrl: string;
  transcoding: boolean;
  subtitleBurnIn: boolean;
  subtitleUrl: string | null;
  // Timeline position where real content begins. > 0 when the server padded
  // the playlist head with placeholders (browser timeline = absolute source
  // timeline); 0 (or absent on an older backend) when the stream covers the
  // whole file and the client must seek the browser itself.
  startOffsetSeconds?: number;
}

const route = useRoute();
const router = useRouter();

// --- Library state ---
const mode = ref<"browse" | "play">("browse");
const movies = ref<LibraryMovie[]>([]);
const seasons = ref<LibrarySeason[]>([]);
const titleSearch = ref("");
const failedPosters = ref(new Set<string>());
const titleOrder = new Intl.Collator(undefined, { sensitivity: "base", numeric: true });
const libraryTitles = computed(() => {
  const shows = new Map<string, LibraryShow>();
  for (const season of seasons.value) {
    // Both season folders and flat episode layouts share the Series/<show> prefix.
    const encodedPath = season.encodedPath.split("/", 2).join("/");
    let show = shows.get(encodedPath);
    if (!show) {
      show = { title: season.title, year: season.year, encodedPath, seasons: [] };
      shows.set(encodedPath, show);
    }
    show.seasons.push(season);
  }
  for (const show of shows.values()) {
    show.seasons.sort((a, b) => a.seasonNumber - b.seasonNumber);
  }
  const titles: LibraryTitle[] = [
    ...movies.value.map(movie => ({ ...movie, type: "movie" as const })),
    ...Array.from(shows.values(), show => ({ ...show, type: "show" as const })),
  ];
  return titles.sort((a, b) =>
    titleOrder.compare(a.title, b.title) || (a.year ?? Infinity) - (b.year ?? Infinity),
  );
});
const visibleTitles = computed(() => {
  const search = titleSearch.value.trim().toLocaleLowerCase();
  return libraryTitles.value.filter(item => item.title.toLocaleLowerCase().includes(search)).map(item => {
    const posterSources = item.type === "movie"
      ? [item.posterUrl]
      : [
          `/api/stream/poster/${item.encodedPath}?seriesPoster=true`,
          ...item.seasons.map(season => season.posterUrl),
        ];
    const paths = item.type === "movie" ? [item.playPath] : [...new Set(item.seasons.flatMap(season => season.episodePaths))];
    return {
      ...item,
      poster: posterSources.find((url): url is string => !!url && !failedPosters.value.has(url)),
      watchStatus: watchStatus(paths, false),
    };
  });
});
const loadingLibrary = ref(false);
const libraryError = ref<string | null>(null);
const currentVideoName = ref("");

// --- Episode overlay ---
const episodeDialog = ref<HTMLDialogElement | null>(null);
const activeShow = ref<LibraryShow | null>(null);
const selectedSeason = ref<LibrarySeason | null>(null);
const episodes = ref<EpisodeInfo[]>([]);
const loadingEpisodes = ref(false);
const episodesError = ref<string | null>(null);

// --- Player state ---
const videoRef = ref<HTMLVideoElement | null>(null);
const videoContainer = ref<HTMLDivElement | null>(null);
const playerFullscreen = ref(false);
const playbackReady = ref(false);
const playbackPaused = ref(true);
const controlsActive = ref(false);
let controlsTimer: ReturnType<typeof setTimeout> | undefined;
const loading = ref(false);
const loadingMessage = ref("Loading video…");
// Ticks once a second while a load/start is in flight so long waits visibly
// progress instead of looking frozen.
const loadingElapsed = ref(0);
const error = ref<string | null>(null);
const starting = ref(false);
// Playback is stalled waiting for the pipeline to produce media at the
// current position (subtitle burn-in sessions decode from zero, so seeking
// into them waits for ffmpeg to catch up). Shows real catch-up progress.
const buffering = ref(false);
const bufferHead = ref(0);
const bufferTarget = ref(0);
const probeInfo = ref<ProbeResponse | null>(null);
const audioChoice = ref<number>(-1);
const subtitleChoice = ref<number>(-1);
const sessionId = ref<string | null>(null);
const subtitleTrackUrl = ref<string | null>(null);
let hls: Hls | null = null;
let playbackVersion = 0;
let heartbeatTimer: ReturnType<typeof setInterval> | null = null;
let saveTimer: ReturnType<typeof setInterval> | null = null;
let loadingTimer: ReturnType<typeof setInterval> | null = null;
let bufferDelayTimer: ReturnType<typeof setTimeout> | null = null;
let bufferPollTimer: ReturnType<typeof setInterval> | null = null;
// Bumped whenever catch-up state is cleared: show/poll work awaiting a fetch
// must not re-create timers or overlays once a later clear has run.
let bufferEpoch = 0;
let resumeTime = 0;
// Timeline position where real content begins. > 0 when the server padded
// the playlist head with placeholder segments so the browser timeline is the
// absolute source timeline (0..full duration) — currentTime is then already
// the absolute position everywhere (progress, seek bar, seek-back restarts).
// 0 means the timeline covers the whole file from zero (no resume, subtitle
// burn-in, placeholder failure or an older backend) and the client must
// perform the initial browser seek itself.
let streamContentStart = 0;
// Whether the pipeline burns subtitles in: such sessions decode from zero
// (the subtitles filter needs source-aligned timestamps) and cannot
// input-seek to a resume or seek target.
let streamBurnIn = false;
// Debounced seek decision in flight (see onVideoSeeking).
let seekTimer: ReturnType<typeof setTimeout> | null = null;
// A settled seek that arrived while a restart was in flight; applied once
// the new stream has started (so late seeks are deferred, not lost).
let pendingSeekTarget: number | null = null;
// Last restartAt target/time: sessions that cannot pad their head would
// otherwise loop (browser seek → beyond head → restart → …).
let lastRestartTarget = -1;
let lastRestartTime = 0;
// Restart cooldown with a deferred re-resolve: at most one session restart per
// window regardless of target. A stalled session retried with a moving playhead
// passed the same-target guard on every step, so the media pipeline was torn
// down and re-attached faster than it could recover (each cycle fired
// pause/play, churned the browser's media stack and reset playback
// triggers). Later seeks are deferred and re-resolved once the window closes —
// resolveSeek re-checks the produced head first, so a stream that has caught up
// costs nothing.
let restartCooldownTimer: ReturnType<typeof setTimeout> | null = null;
let deferredRestartTarget: number | null = null;
// Path captured when playback loads. Progress/watched keys must use this
// rather than the live route computed: by the time unmount or a stray timer
// tick runs, the route may already point at a different (or no) video.
const currentPath = ref("");
const nextEpisode = computed(() => {
  const showPath = currentPath.value.split("/", 2).join("/");
  const show = libraryTitles.value.find(item => item.encodedPath === showPath);
  if (!show || show.type !== "show") return null;

  const paths = [...new Set(show.seasons.flatMap(season => season.episodePaths))];
  const index = paths.indexOf(currentPath.value);
  if (index < 0 || index + 1 >= paths.length) return null;
  const path = paths[index + 1];
  return { path, showPath, season: show.seasons.find(season => season.episodePaths.includes(path)) };
});

function showPlayerControls() {
  controlsActive.value = true;
  clearTimeout(controlsTimer);
  controlsTimer = setTimeout(hidePlayerControls, 3000);
}

function hidePlayerControls() {
  clearTimeout(controlsTimer);
  controlsActive.value = false;
}

function onFullscreenChange() {
  playerFullscreen.value = !!videoContainer.value && document.fullscreenElement === videoContainer.value;
  showPlayerControls();
}

async function setPlayerFullscreen(fullscreen: boolean) {
  const container = videoContainer.value;
  if (!container || fullscreen === (document.fullscreenElement === container)) return;
  try {
    // Request fullscreen for the whole player so overlay buttons can receive input.
    if (fullscreen) await container.requestFullscreen();
    else await document.exitFullscreen();
  } catch (error) {
    console.warn("Could not change fullscreen mode", error);
  }
}

function toggleFullscreen() {
  if (playbackReady.value) void setPlayerFullscreen(document.fullscreenElement !== videoContainer.value);
}

function togglePlayback() {
  const video = videoRef.value;
  if (!video || !playbackReady.value) return;
  if (video.paused) {
    void video.play().catch(() => { /* playback may still be loading */ });
  } else {
    video.pause();
  }
}

function onVideoClick(event: MouseEvent) {
  // Both clicks cancel out the playback toggle when double-clicking for fullscreen.
  if (event.detail === 1 || event.detail === 2) togglePlayback();
}

function playNextEpisode() {
  const next = nextEpisode.value;
  if (!next) return;
  if (next.season && activeShow.value?.encodedPath === next.showPath) {
    selectedSeason.value = next.season;
  }
  playFromLibrary(next.path);
}

// --- Loading / buffering feedback ---
function beginLoading(message: string) {
  loading.value = true;
  loadingMessage.value = message;
  loadingElapsed.value = 0;
  if (loadingTimer) clearInterval(loadingTimer);
  loadingTimer = setInterval(() => {
    loadingElapsed.value += 1;
  }, 1000);
  endBuffering();
}

function endLoading() {
  loading.value = false;
  if (loadingTimer) {
    clearInterval(loadingTimer);
    loadingTimer = null;
  }
}

function endBuffering() {
  bufferEpoch += 1;
  buffering.value = false;
  if (bufferDelayTimer) {
    clearTimeout(bufferDelayTimer);
    bufferDelayTimer = null;
  }
  if (bufferPollTimer) {
    clearInterval(bufferPollTimer);
    bufferPollTimer = null;
  }
}

// The produced head of one specific session: callers pin the id so a restart
// mid-fetch cannot label the current stream with the old session's state.
async function fetchStreamHead(id: string): Promise<number | null> {
  try {
    const res = await apiGet<{ headSeconds: number | null }>(`stream/${id}/head`);
    return typeof res.headSeconds === "number" ? res.headSeconds : null;
  } catch {
    return null;
  }
}

// The video fires `waiting` for any stall; only treat it as catch-up work when
// media at the position is genuinely not produced yet (brief stalls play again
// on their own and must not flash an overlay).
function scheduleBufferCheck() {
  if (loading.value || buffering.value) return;
  if (bufferDelayTimer) clearTimeout(bufferDelayTimer);
  bufferDelayTimer = setTimeout(() => {
    bufferDelayTimer = null;
    void beginBuffering();
  }, 700);
}

async function beginBuffering() {
  if (loading.value || starting.value || buffering.value) return;
  const id = sessionId.value;
  if (!id) return;
  const target = videoRef.value?.currentTime ?? 0;
  const epoch = bufferEpoch;
  const head = await fetchStreamHead(id);
  // Re-check after the await: playback may have resumed, a restart may have
  // swapped the session, or cleanup may have run meanwhile. An unknown head is
  // never rendered as progress.
  if (epoch !== bufferEpoch || sessionId.value !== id) return;
  if (loading.value || starting.value || head === null || target <= head + 2) return;
  bufferTarget.value = target;
  bufferHead.value = head;
  buffering.value = true;
  if (bufferPollTimer) clearInterval(bufferPollTimer);
  bufferPollTimer = setInterval(() => void tickBufferHead(id), 1000);
}

// Hide only once the target itself is produced: the segment containing it is
// published at that point and playback resumes within an hls.js retry.
async function tickBufferHead(id: string) {
  const head = await fetchStreamHead(id);
  if (!buffering.value) return;
  if (sessionId.value !== id) {
    endBuffering();
    return;
  }
  if (head === null) return;
  bufferHead.value = head;
  if (head >= bufferTarget.value) endBuffering();
}

function onVideoWaiting() {
  scheduleBufferCheck();
}

function onVideoPlaying() {
  endBuffering();
}

const bufferDetail = computed(() => {
  const total = probeInfo.value?.durationSeconds ?? 0;
  const ready = `Ready through ${formatTime(bufferHead.value)}${total > 0 ? ` of ${formatTime(total)}` : ""}`;
  return bufferTarget.value > bufferHead.value
    ? `${ready} · waiting for ${formatTime(bufferTarget.value)}`
    : ready;
});

const bufferPercent = computed(() => {
  const total = probeInfo.value?.durationSeconds ?? 0;
  if (total <= 0) return 0;
  return Math.min(100, (bufferHead.value / total) * 100);
});

// --- Progress persistence ---
function progressKey() {
  return `video-progress:${currentPath.value}`;
}
const watchedPrefix = "video-watched:";
const watchedPaths = ref(new Set(Object.keys(localStorage)
  .filter(key => key.startsWith(watchedPrefix) && localStorage.getItem(key) === "1")
  .map(key => key.slice(watchedPrefix.length))));

function isWatched(path: string): boolean {
  return watchedPaths.value.has(path);
}

function setWatched(path: string, watched: boolean) {
  if (!path) return;
  if (watched) {
    localStorage.setItem(watchedPrefix + path, "1");
    localStorage.removeItem("video-progress:" + path);
    watchedPaths.value.add(path);
  } else {
    localStorage.removeItem(watchedPrefix + path);
    watchedPaths.value.delete(path);
  }
}

function watchStatus(paths: string[], showUnwatched = true): string {
  const watched = paths.filter(isWatched).length;
  if (!paths.length || (!watched && !showUnwatched)) return "";
  return watched === paths.length ? "Watched" : `${watched} of ${paths.length} watched`;
}

function saveProgress() {
  const v = videoRef.value;
  if (!v || !currentPath.value || v.currentTime < 1) return;
  // The HLS timeline is the absolute source timeline (the server pads the
  // head with placeholders when the pipeline was input-seeked), so the
  // browser's currentTime is already the position in the source file.
  const absolute = Math.floor(v.currentTime);
  // Don't save if we're near the end (>95% complete) — credits can run long,
  // so treat reaching them as having watched the episode.
  const total = probeInfo.value?.durationSeconds ?? 0;
  const fraction = total > 0
    ? absolute / total
    : v.duration > 0 ? v.currentTime / v.duration : 0;
  if (fraction > 0.95) {
    setWatched(currentPath.value, true);
    return;
  }
  localStorage.setItem(progressKey(), String(absolute));
}
function loadProgress(): number {
  const raw = localStorage.getItem(progressKey());
  if (!raw) return 0;
  const t = parseInt(raw, 10);
  // Re-enter before the saved point to restore scene context and absorb
  // timestamp differences between browser playback and HLS segments.
  return isNaN(t) ? 0 : Math.max(0, t - 30);
}
function onVideoEnded() {
  playbackPaused.value = true;
  setWatched(currentPath.value, true);
}
function onVideoPlay() {
  playbackPaused.value = false;
  showPlayerControls();
}
function onVideoPause() {
  playbackPaused.value = true;
  if (!videoRef.value?.ended) saveProgress();
}
function startSaveTimer() {
  stopSaveTimer();
  saveTimer = setInterval(saveProgress, 10_000);
}
function stopSaveTimer() {
  if (saveTimer) {
    clearInterval(saveTimer);
    saveTimer = null;
  }
}

const encodedPath = computed<string>(() => {
  const m = route.params.pathMatch;
  if (!m) return "";
  return Array.isArray(m) ? (m as string[]).filter(Boolean).join("/") : String(m);
});

// --- Library logic ---
async function loadLibrary() {
  loadingLibrary.value = true;
  libraryError.value = null;
  try {
    const res = await apiGet<LibraryResponse>("stream/library");
    movies.value = res.movies ?? [];
    seasons.value = res.seasons ?? [];
  } catch (e: unknown) {
    libraryError.value = e instanceof Error ? e.message : String(e);
  } finally {
    loadingLibrary.value = false;
  }
}

async function playFromLibrary(path: string) {
  const openingFromLibrary = mode.value === "browse";
  const failure = await router.push(`/video/${path}`);
  if (!openingFromLibrary || failure) return;
  // Use the selection's user activation as soon as Vue has mounted the player.
  await nextTick();
  if (encodedPath.value === path) await setPlayerFullscreen(true);
}

function seasonLabel(count: number): string {
  return count === 1 ? "1 season" : `${count} seasons`;
}

function seasonKey(season: LibrarySeason): string {
  return `${season.encodedPath}:${season.seasonNumber}`;
}

function openTitle(item: LibraryTitle) {
  if (item.type === "movie") {
    playFromLibrary(item.playPath);
    return;
  }
  activeShow.value = item;
  const lastSeason = localStorage.getItem(`video-season:${item.encodedPath}`);
  selectedSeason.value = item.seasons.find(season => seasonKey(season) === lastSeason)
    ?? item.seasons.find(season => season.seasonNumber > 0 && season.episodePaths.length)
    ?? item.seasons[0];
}

function closeShow() {
  episodeDialog.value?.close();
  activeShow.value = null;
  selectedSeason.value = null;
}

watch(episodeDialog, dialog => dialog?.showModal());

watch(selectedSeason, async (season, previousSeason, onCleanup) => {
  if (!season) return;
  if (previousSeason && activeShow.value) {
    localStorage.setItem(`video-season:${activeShow.value.encodedPath}`, seasonKey(season));
  }
  let current = true;
  onCleanup(() => { current = false; });
  loadingEpisodes.value = true;
  episodesError.value = null;
  episodes.value = [];
  try {
    // A flat series can use the same folder for every season.
    const res = await apiGet<EpisodesResponse>(
      `stream/episodes/${season.encodedPath}?season=${season.seasonNumber}`,
    );
    if (current) episodes.value = res.episodes ?? [];
  } catch (error) {
    if (current) episodesError.value = error instanceof Error ? error.message : "Could not load episodes.";
  } finally {
    if (current) loadingEpisodes.value = false;
  }
});

// Human-friendly "S1E2"-style badge for the left of each row.
function episodeBadge(ep: EpisodeInfo): string {
  if (ep.season == null && ep.episode == null) return "EP";
  return `S${ep.season ?? "?"}E${ep.episode ?? "?"}`;
}

// Primary label: the parsed descriptive title when available, otherwise a
// simple "Episode N". Falls back to the cleaned file name if neither season
// nor episode could be determined.
function episodeLabel(ep: EpisodeInfo): string {
  if (ep.title) return ep.title;
  if (ep.episode != null) return `Episode ${ep.episode}`;
  return ep.fileName;
}

function formatSize(n: number): string {
  if (!n) return "—";
  if (n < 1024) return `${n} B`;
  const units = ["KB", "MB", "GB", "TB"];
  let v = n / 1024;
  let i = 0;
  while (v >= 1024 && i < units.length - 1) {
    v /= 1024;
    i++;
  }
  return `${v.toFixed(v >= 10 ? 0 : 1)} ${units[i]}`;
}

// m:ss, or h:mm:ss past an hour.
function formatTime(seconds: number): string {
  const s = Math.max(0, Math.floor(seconds));
  const h = Math.floor(s / 3600);
  const m = Math.floor((s % 3600) / 60);
  const sec = s % 60;
  const mm = h > 0 ? String(m).padStart(2, "0") : String(m);
  return `${h > 0 ? `${h}:` : ""}${mm}:${String(sec).padStart(2, "0")}`;
}

// --- Player logic ---
function audioLabel(a: ProbeAudioStream): string {
  const lang = a.language && a.language !== "und" ? a.language : "Unknown language";
  const title = a.title ? ` — ${a.title}` : "";
  const details = a.isDefault ? " [default]" : "";
  return `Track ${a.index} — ${lang} (${a.codec})${title}${details}`;
}

function subtitleLabel(s: ProbeSubtitleStream): string {
  const lang = s.language && s.language !== "und" ? s.language : "Unknown language";
  const title = s.title ? ` — ${s.title}` : "";
  const burn = s.canDeliverAsVtt ? "" : " [burn]";
  const flags = [s.isForced ? "forced" : "", s.isHearingImpaired ? "SDH" : "", s.isDefault ? "default" : ""].filter(Boolean);
  const details = flags.length ? ` [${flags.join(", ")}]` : "";
  return `Track ${s.index} — ${lang} (${s.codec})${title}${details}${burn}`;
}

const selectedSubtitleLabel = computed(() => {
  const selected = probeInfo.value?.subtitles.find((s) => s.index === subtitleChoice.value);
  return selected ? subtitleLabel(selected) : "Subtitles";
});

function onSubtitleLoaded(event: Event) {
  // eslint-disable-next-line no-undef -- DOM type supplied by TypeScript
  const element = event.target as HTMLTrackElement | null;
  if (element) element.track.mode = "showing";
}

function onLoadedMetadata() {
  playbackReady.value = true;
  playbackPaused.value = videoRef.value?.paused ?? true;
  endLoading();
  // Padded streams already begin at the content (the server input-seeked the
  // pipeline and hls.js starts at streamContentStart); only seek the browser
  // when the timeline covers the whole file from zero — subtitle burn-in,
  // placeholder failure, an older backend — or when the native HLS path
  // (Safari) plays a padded playlist, since hls.js's startPosition doesn't
  // apply there.
  const target = resumeTime > 0
    ? resumeTime
    : streamContentStart > 0 && !hls ? streamContentStart : 0;
  if (target > 0 && videoRef.value) {
    try {
      videoRef.value.currentTime = target;
    } catch {
      /* ignore */
    }
  }
  resumeTime = 0;
}

function onError() {
  endLoading();
  endBuffering();
  error.value = "Failed to load video. The file may be corrupted or an unsupported format.";
}

function stopPlayback() {
  saveProgress();
  playbackVersion += 1;
  currentPath.value = "";
  playbackReady.value = false;
  starting.value = false;
  subtitleTrackUrl.value = null;
  streamContentStart = 0;
  streamBurnIn = false;
  hidePlayerControls();
  endLoading();
  endBuffering();
  if (seekTimer) {
    clearTimeout(seekTimer);
    seekTimer = null;
  }
  pendingSeekTarget = null;
  // A deferred restart must never fire after teardown.
  if (restartCooldownTimer) {
    clearTimeout(restartCooldownTimer);
    restartCooldownTimer = null;
  }
  deferredRestartTarget = null;
  lastRestartTarget = -1;
  lastRestartTime = 0;
  void stopCurrentSession();
  stopSaveTimer();
  teardownHls();
  currentVideoName.value = "";
  probeInfo.value = null;
  error.value = null;
}

function exitPlayer() {
  mode.value = "browse";
  stopPlayback();
  router.push("/video");
}

async function loadProbe() {
  const path = encodedPath.value;
  const version = playbackVersion;
  currentPath.value = path;
  if (!seasons.value.length && !loadingLibrary.value && decodeSegment(path.split("/")[0]) === "Series") {
    void loadLibrary();
  }
  beginLoading("Probing media…");
  error.value = null;
  // Restore saved position (if any)
  resumeTime = loadProgress();
  try {
    const info = await apiGet<ProbeResponse>(`stream/probe/${path}`);
    if (version !== playbackVersion) return;
    probeInfo.value = info;
    const defaultAudio = info.audio.find((a) => a.isDefault) ?? info.audio[0];
    audioChoice.value = defaultAudio?.index ?? -1;
    const defaultSub = info.subtitles.find((s) => s.isDefault);
    subtitleChoice.value = defaultSub?.index ?? -1;
    await startStream();
  } catch (e: any) {
    if (version !== playbackVersion) return;
    endLoading();
    error.value = e?.message ? `Probe failed: ${e.message}` : "Probe failed.";
  }
}

async function startStream() {
  const version = playbackVersion;
  playbackReady.value = false;
  starting.value = true;
  // Burned-in subtitles need the whole video transcoded, so say so instead of
  // leaving a bare spinner while the first segment is produced.
  const burnInSelected =
    probeInfo.value?.subtitles.find((s) => s.index === subtitleChoice.value)?.canDeliverAsVtt === false;
  beginLoading(
    burnInSelected
      ? "Burning in subtitles — transcoding the video before playback…"
      : "Starting stream…",
  );
  error.value = null;

  await stopCurrentSession();
  if (version !== playbackVersion) return;
  teardownHls();

  try {
    const resp = await apiPost<StartStreamResponse>("stream/start", {
      encodedPath: currentPath.value,
      audioStreamIndex: audioChoice.value >= 0 ? audioChoice.value : null,
      subtitleStreamIndex: subtitleChoice.value >= 0 ? subtitleChoice.value : null,
      // Resume by input-seeking the ffmpeg pipeline straight to the saved
      // position, so playback begins there immediately. Seeking the browser
      // into a freshly started stream instead means waiting for ffmpeg to
      // produce the segment at that position while hls.js backs off on 404s.
      // The server reports the offset it applied; when it can't seek
      // (subtitle burn-in) the timeline covers the whole file and
      // onLoadedMetadata falls back to the browser-side seek.
      startTimeSeconds: resumeTime > 0 ? Math.floor(resumeTime) : null,
    });
    if (version !== playbackVersion) {
      void apiPost(`stream/${resp.sessionId}/stop`).catch(() => {});
      return;
    }
    sessionId.value = resp.sessionId;
    subtitleTrackUrl.value = resp.subtitleUrl;
    streamContentStart = resp.startOffsetSeconds ?? 0;
    streamBurnIn = resp.subtitleBurnIn;
    // The server consumed the resume position (padded head, pipeline seeked
    // there) when streamContentStart > 0; only fall back to a browser seek
    // when the timeline starts at zero.
    if (streamContentStart > 0) resumeTime = 0;
    await attachHls(resp.playlistUrl, streamContentStart);
    if (version === playbackVersion) startHeartbeat();
  } catch (e: any) {
    if (version === playbackVersion) {
      endLoading();
      endBuffering();
      error.value = e?.message ? `Stream start failed: ${e.message}` : "Stream start failed.";
    }
  } finally {
    if (version === playbackVersion) {
      starting.value = false;
      if (pendingSeekTarget !== null) {
        // A settled seek arrived mid-restart: resolve it against the new stream.
        const target = pendingSeekTarget;
        pendingSeekTarget = null;
        void resolveSeek(target);
      }
    }
  }
}

async function onTrackChange() {
  if (!probeInfo.value) return;
  if (videoRef.value) {
    // The browser timeline is the absolute source timeline.
    resumeTime = videoRef.value.currentTime;
  }
  await startStream();
}

// Seeking fires per scrub step, so settle before resolving the final target.
function onVideoSeeking() {
  const v = videoRef.value;
  if (!v || !currentPath.value) return;
  showPlayerControls();
  const target = v.currentTime;
  if (seekTimer) clearTimeout(seekTimer);
  seekTimer = setTimeout(() => {
    seekTimer = null;
    if (buffering.value) {
      // Re-targeted a visible catch-up: track the new position.
      bufferTarget.value = target;
    } else if (v.paused) {
      // `waiting` does not fire reliably while paused (Safari), so a paused
      // scrub into unproduced media needs the check armed explicitly.
      scheduleBufferCheck();
    }
    void resolveSeek(target);
  }, 500);
}

// Media is produced strictly in order from the stream head, so two kinds of
// seek need a session restart instead of a plain browser seek:
//   - into the padded placeholder head (black video + silence, not media);
//   - past the transcode head, where the segment does not exist yet and
//     hls.js would poll 404s until ffmpeg catches up — potentially minutes.
// Everything the pipeline has already produced loads directly; restarting
// input-seeks the pipeline to the target and completes in seconds.
async function resolveSeek(target: number) {
  // A settled seek supersedes any deferred restart from an earlier cooldown:
  // without this, a stale target could survive a no-restart resolution and
  // later yank playback backwards to it.
  deferredRestartTarget = null;
  if (starting.value) {
    pendingSeekTarget = target; // a restart is in flight: settle again after it
    return;
  }
  if (streamContentStart > 0 && target < streamContentStart) {
    restartAt(target);
    return;
  }
  // Burn-in sessions decode from zero and cannot input-seek, so a restart
  // would not help; wait for the pipeline to reach the target instead.
  if (streamBurnIn) return;
  const id = sessionId.value;
  if (!id) return;
  try {
    const res = await apiGet<{ headSeconds: number }>(`stream/${id}/head`);
    const head = typeof res.headSeconds === "number" ? res.headSeconds : null;
    // A segment or two of slack is cheaper to wait out than a restart. Re-check
    // state after the await: a track change or restart may have superseded it.
    if (head === null || starting.value || sessionId.value !== id) return;
    if (target > head + 8) restartAt(target);
  } catch {
    return; // no head information: keep the previous stall-and-retry behaviour
  }
}

function restartAt(target: number) {
  const floored = Math.floor(target);
  const now = Date.now();
  if (floored === lastRestartTarget && now - lastRestartTime < 10_000) return;
  if (now - lastRestartTime < 15_000) {
    deferredRestartTarget = floored;
    if (restartCooldownTimer === null) {
      restartCooldownTimer = setTimeout(() => {
        restartCooldownTimer = null;
        const deferred = deferredRestartTarget;
        deferredRestartTarget = null;
        if (deferred !== null) void resolveSeek(deferred);
      }, 15_000 - (now - lastRestartTime));
    }
    return;
  }
  if (restartCooldownTimer !== null) {
    clearTimeout(restartCooldownTimer);
    restartCooldownTimer = null;
  }
  deferredRestartTarget = null;
  lastRestartTarget = floored;
  lastRestartTime = now;
  pendingSeekTarget = null;
  resumeTime = floored;
  void startStream();
}

function attachHls(playlistUrl: string, startAt = 0): Promise<void> {
  return new Promise((resolve, reject) => {
    const video = videoRef.value;
    if (!video) {
      reject(new Error("No video element"));
      return;
    }

    if (Hls.isSupported()) {
      hls = new Hls({
        xhrSetup: (xhr) => {
          xhr.withCredentials = true;
        },
        enableWorker: true,
        lowLatencyMode: false,
        // Start straight at the content: with a padded playlist the head is
        // placeholder media, and starting at 0 would play black first.
        startPosition: startAt,
        fragLoadingMaxRetry: 200,
        fragLoadingRetryDelay: 500,
        // Cap the retry backoff well below hls.js's 64s default. Segments
        // ahead of ffmpeg's head don't exist yet, and a long backoff leaves
        // a seek into them stalled for up to a minute after they appear.
        fragLoadingMaxRetryTimeout: 2000,
        manifestLoadingMaxRetry: 10,
        levelLoadingMaxRetry: 10,
      });
      hls.loadSource(playlistUrl);
      hls.attachMedia(video);
      hls.on(Hls.Events.DESTROYING, () => resolve());
      hls.on(Hls.Events.MANIFEST_PARSED, () => {
        endLoading();
        startSaveTimer();
        video.play().catch(() => {
          /* autoplay may be blocked */
        });
        resolve();
      });
      hls.on(Hls.Events.ERROR, (_evt, data) => {
        if (!data.fatal) return;
        if (
          data.type === Hls.ErrorTypes.NETWORK_ERROR &&
          data.details === Hls.ErrorDetails.FRAG_LOAD_ERROR
        ) {
          hls?.startLoad();
        } else {
          reject(new Error(`HLS error: ${data.type} / ${data.details}`));
        }
      });
      return;
    }

    if (video.canPlayType("application/vnd.apple.mpegurl")) {
      video.src = playlistUrl;
      video.addEventListener(
        "loadedmetadata",
        () => {
          startSaveTimer();
          resolve();
        },
        { once: true },
      );
      video.addEventListener("error", () => reject(new Error("Native HLS error")), { once: true });
      return;
    }

    reject(new Error("HLS not supported in this browser"));
  });
}

function teardownHls() {
  if (hls) {
    hls.destroy();
    hls = null;
  }
  if (videoRef.value) {
    videoRef.value.removeAttribute("src");
    videoRef.value.load();
  }
}

function startHeartbeat() {
  stopHeartbeat();
  heartbeatTimer = setInterval(async () => {
    const id = sessionId.value;
    if (!id) return;
    try {
      await apiPost(`stream/${id}/heartbeat`);
    } catch {
      /* ignore */
    }
  }, 60_000);
}

function stopHeartbeat() {
  if (heartbeatTimer) {
    clearInterval(heartbeatTimer);
    heartbeatTimer = null;
  }
}

async function stopCurrentSession() {
  const id = sessionId.value;
  if (!id) return;
  sessionId.value = null;
  stopHeartbeat();
  try {
    await apiPost(`stream/${id}/stop`);
  } catch {
    /* best effort */
  }
}

// --- Route handling ---
function decodeSegment(seg: string): string {
  try {
    const padded = seg.replace(/-/g, "+").replace(/_/g, "/");
    const pad = padded.length % 4 ? "=".repeat(4 - (padded.length % 4)) : "";
    const bin = atob(padded + pad);
    try {
      return decodeURIComponent(escape(bin));
    } catch {
      return bin;
    }
  } catch {
    return seg;
  }
}

function isVideoFileName(name: string): boolean {
  const ext = name.split(".").pop()?.toLowerCase() ?? "";
  return ["mp4", "webm", "mov", "mkv", "avi", "m4v", "ogv", "ts", "wmv", "flv"].includes(ext);
}

function syncModeFromRoute() {
  if (!encodedPath.value) {
    mode.value = "browse";
    return;
  }
  const segments = encodedPath.value.split("/");
  const last = decodeSegment(segments[segments.length - 1]);
  if (isVideoFileName(last)) {
    mode.value = "play";
    currentVideoName.value = last;
  } else {
    mode.value = "browse";
  }
}

// Capture shortcuts before the native controls, including after the video loses focus.
function onPlayerKeydown(e: KeyboardEvent) {
  const video = videoRef.value;
  if (!video || mode.value !== "play" || e.altKey || e.ctrlKey || e.metaKey) return;
  if (e.target !== video && e.target !== document.body) return;

  // Keep play/pause available after an arrow-key seek moves focus off the video.
  if (e.key === " " && e.target === document.body) {
    e.preventDefault();
    if (!e.repeat) togglePlayback();
    return;
  }

  if (e.key !== "ArrowRight" && e.key !== "ArrowLeft") return;
  e.preventDefault();
  e.stopPropagation();
  // Keyboard focus draws a ring and keeps Chromium's controls and cursor visible.
  if (document.activeElement === video) video.blur();
  video.currentTime += e.key === "ArrowRight" ? 5 : -5;
}

watch(
  encodedPath,
  async () => {
    if (!route.path.startsWith("/video")) return;
    // Save against the old path before switching episodes or leaving the player.
    if (mode.value === "play") stopPlayback();
    syncModeFromRoute();
    if (mode.value === "browse") {
      await loadLibrary();
    } else {
      await loadProbe();
    }
  },
);

onMounted(() => {
  document.addEventListener("fullscreenchange", onFullscreenChange);
  window.addEventListener("keydown", onPlayerKeydown, true);
  window.addEventListener("pagehide", saveProgress);
  syncModeFromRoute();
  if (mode.value === "browse") {
    loadLibrary();
  } else {
    loadProbe();
  }
});

onBeforeUnmount(() => {
  document.removeEventListener("fullscreenchange", onFullscreenChange);
  window.removeEventListener("keydown", onPlayerKeydown, true);
  window.removeEventListener("pagehide", saveProgress);
  stopHeartbeat();
  stopPlayback();
});
</script>

<style scoped>
.video-page {
  height: 100%;
}

/* ===== Library mode ===== */
.library-mode {
  background: radial-gradient(ellipse 70% 17rem at 0 0, #192d48, transparent), var(--theatre-base);
  padding: 1.125rem clamp(1rem, 2.25vw, 2.25rem) 2rem;
  overflow-y: auto;
  height: 100%;
}

.library-header {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  justify-content: space-between;
  gap: 1rem;
  border-bottom: 1px solid var(--theatre-border);
  padding-bottom: 0.875rem;
  margin-bottom: 1.25rem;
}

.library-brand {
  display: flex;
  align-items: center;
  gap: 14px;
}

.library-mark {
  display: block;
  flex-shrink: 0;
}

.library-brand h1 {
  margin: 0 0 4px;
  font-size: 23px;
  letter-spacing: 0.025em;
  line-height: 1.2;
}

.library-brand p {
  margin: 0;
  color: var(--theatre-muted);
  font-size: 10px;
  line-height: 1.2;
  letter-spacing: 0.13em;
  text-transform: uppercase;
}

.library-search {
  display: flex;
  align-items: center;
  gap: 0.5rem;
  width: clamp(13rem, 24vw, 19rem);
  max-width: 100%;
  min-height: 38px;
  padding: 0 0.65rem;
  border: 1px solid var(--theatre-border);
  border-radius: 8px;
  background: var(--theatre-panel);
  color: var(--theatre-muted);
}

.library-search:focus-within {
  border-color: var(--theatre-accent);
}

.library-search .material-symbols-outlined {
  font-size: 19px;
}

.library-search input {
  width: 100%;
  min-width: 0;
  padding: 0.5rem 0;
  border: 0;
  outline: none;
  background: transparent;
  color: var(--text);
  font: inherit;
  font-size: 0.875rem;
}

.library-search input::placeholder {
  color: var(--theatre-muted);
}

.poster-grid {
  display: grid;
  grid-template-columns: repeat(auto-fill, minmax(min(100%, clamp(156px, 14.5vw, 224px)), 1fr));
  gap: clamp(0.75rem, 1.5vw, 1.25rem);
  list-style: none;
  margin: 0;
  padding: 0;
}

.poster-grid li {
  min-width: 0;
}

.poster-card {
  display: flex;
  flex-direction: column;
  width: 100%;
  height: 100%;
  padding: 0;
  border: 1px solid var(--theatre-border);
  border-radius: 9px;
  overflow: hidden;
  background: var(--theatre-panel);
  color: var(--text);
  text-align: left;
  cursor: pointer;
  transition: border-color 0.15s, background-color 0.15s;
}

.poster-card:hover {
  border-color: var(--theatre-border-strong);
  background: var(--theatre-raised);
}

.library-mode button:focus-visible,
.season-bar select:focus-visible {
  outline: 2px solid var(--theatre-accent);
  outline-offset: 2px;
}

.poster-art {
  position: relative;
  display: grid;
  place-items: center;
  width: 100%;
  aspect-ratio: 2 / 3;
  background: radial-gradient(ellipse at 45% 32%, #233958, var(--theatre-panel) 75%);
  color: var(--theatre-accent);
}

.poster-art .material-symbols-outlined {
  font-size: 2rem;
}

.poster-img {
  position: absolute;
  inset: 0;
  width: 100%;
  height: 100%;
  object-fit: cover;
}

.poster-info {
  display: flex;
  flex-direction: column;
  gap: 0.2rem;
  width: 100%;
  padding: 0.65rem 0.75rem 0.7rem;
}

.poster-title {
  font-size: 0.875rem;
  font-weight: 650;
  line-height: 1.3;
  overflow: hidden;
  display: -webkit-box;
  -webkit-line-clamp: 2;
  -webkit-box-orient: vertical;
  overflow-wrap: anywhere;
}

.poster-meta,
.poster-watched {
  font-size: 0.75rem;
  color: var(--theatre-muted);
}

.poster-watched {
  color: var(--theatre-accent);
}

.library-mode .status,
.library-mode .empty {
  margin: 1.5rem 0;
  color: var(--theatre-muted);
}

.library-mode .error {
  margin: 1.5rem 0;
  color: #f5aaa2;
}

/* ===== Episode overlay ===== */
.episode-dialog {
  width: min(640px, calc(100% - 2rem));
  margin: auto;
  padding: 0;
  border: 1px solid var(--theatre-border-strong);
  border-radius: 12px;
  overflow: hidden;
  background: var(--theatre-panel);
  color: var(--text);
  box-shadow: 0 18px 60px #0009;
}

.episode-dialog::backdrop {
  background: #000a;
}

.episode-panel {
  display: flex;
  flex-direction: column;
  min-height: min(16rem, 80dvh);
  max-height: 80dvh;
}

.episode-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 0.75rem;
  padding: 0.75rem 1rem;
  border-bottom: 1px solid var(--theatre-border);
}

.episode-header h2 {
  margin: 0;
  font-size: 1.125rem;
  font-weight: 600;
  overflow-wrap: anywhere;
}

.close-btn {
  display: grid;
  place-items: center;
  flex-shrink: 0;
  width: 32px;
  height: 32px;
  padding: 0;
  border: 0;
  border-radius: 6px;
  background: transparent;
  color: var(--theatre-muted);
  cursor: pointer;
}

.close-btn:hover {
  background: var(--theatre-hover);
  color: var(--text);
}

.season-bar {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  justify-content: space-between;
  gap: 0.5rem 1rem;
  padding: 0.625rem 1rem;
  border-bottom: 1px solid var(--theatre-border);
}

.season-bar select {
  padding: 0.375rem 0.625rem;
  border: 1px solid var(--theatre-border-strong);
  border-radius: 6px;
  background: var(--theatre-raised);
  color: var(--text);
  font-size: 0.875rem;
  cursor: pointer;
}

.season-progress,
.episode-message {
  color: var(--theatre-muted);
  font-size: 0.875rem;
}

.episode-message {
  padding: 0 1rem;
}

.episode-message[role="alert"] {
  color: #f5aaa2;
}

.episode-list {
  overflow-y: auto;
  padding: 0.5rem;
}

.episode-row { display: flex; }
.episode-toggle {
  border: 0;
  border-bottom: 1px solid var(--theatre-border);
  padding: 0 0.75rem;
  background: none;
  color: var(--theatre-muted);
  cursor: pointer;
}
.episode-toggle:hover { background: var(--theatre-hover); color: var(--text); }
.episode-toggle .material-symbols-outlined { font-size: 1.15rem; }

.episode-item {
  flex: 1;
  min-width: 0;
  display: flex;
  align-items: center;
  width: 100%;
  padding: 0.6rem 0.75rem;
  background: none;
  border: none;
  border-radius: 6px;
  cursor: pointer;
  text-align: left;
  font: inherit;
  color: var(--text);
  gap: 0.75rem;
}

.episode-item:hover {
  background: var(--theatre-hover);
}

.episode-badge {
  flex-shrink: 0;
  min-width: 2.6rem;
  text-align: center;
  padding: 0.15rem 0.3rem;
  border-radius: 4px;
  background: var(--theatre-raised);
  color: var(--theatre-muted);
  font-size: 0.68rem;
  font-weight: 600;
  letter-spacing: 0.02em;
}

.episode-main {
  flex: 1;
  min-width: 0;
  display: flex;
  flex-direction: column;
  gap: 0.1rem;
}

.episode-name {
  font-size: 0.85rem;
  font-weight: 500;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.episode-file {
  font-size: 0.7rem;
  color: var(--theatre-muted);
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.episode-size {
  flex-shrink: 0;
  font-size: 0.75rem;
  color: var(--theatre-muted);
  white-space: nowrap;
}

.episode-watched {
  flex-shrink: 0;
  padding: 0.15rem 0.5rem;
  border-radius: 999px;
  background: var(--theatre-raised);
  color: var(--theatre-accent);
  font-size: 0.65rem;
  font-weight: 600;
  letter-spacing: 0.02em;
}

/* ===== Player mode ===== */
.player-mode {
  display: flex;
  flex-direction: column;
  height: 100%;
  background: #000;
  color: #fff;
}

.player-toolbar {
  position: absolute;
  inset: 0 0 auto;
  z-index: 2;
  display: grid;
  grid-template-columns: minmax(0, 1fr) auto;
  align-items: center;
  gap: 0.5rem 1.25rem;
  padding: 0.875rem clamp(1rem, 2.25vw, 2.25rem) 2rem;
  background: linear-gradient(to bottom, #101620f2, #101620cc 52%, transparent);
}

.player-heading {
  display: flex;
  align-items: center;
  min-width: 0;
  gap: 0.75rem;
}

.back-btn {
  display: grid;
  place-items: center;
  width: 38px;
  height: 38px;
  flex-shrink: 0;
  padding: 0;
  border: 1px solid var(--theatre-border-strong);
  border-radius: 8px;
  background: var(--theatre-panel);
  color: var(--theatre-accent);
  cursor: pointer;
  transition: background 0.15s;
}

.back-btn:hover {
  background: var(--theatre-hover);
}

.back-btn .material-symbols-outlined {
  font-size: 20px;
}

.player-heading h1 {
  margin: 0;
  font-size: clamp(1rem, 1.45vw, 1.25rem);
  font-weight: 600;
  line-height: 1.3;
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
  text-shadow: 0 1px 4px #000;
}

.track-bar {
  display: flex;
  align-items: center;
  gap: 0.5rem 0.875rem;
  flex-wrap: wrap;
}

.track-field {
  display: flex;
  align-items: center;
  gap: 0.5rem;
  font-size: 0.85rem;
}

.track-label {
  color: var(--text);
  text-shadow: 0 1px 4px #000;
}

.track-field select {
  width: clamp(7rem, 15vw, 12rem);
  min-height: 34px;
  background: var(--theatre-panel);
  color: var(--text);
  border: 1px solid var(--theatre-border-strong);
  border-radius: 4px;
  padding: 0.3rem 0.5rem;
  font-size: 0.85rem;
  cursor: pointer;
}

.track-field select:disabled {
  opacity: 0.5;
  cursor: not-allowed;
}

@media (max-width: 760px) {
  .player-toolbar {
    grid-template-columns: minmax(0, 1fr);
  }

  .track-field select {
    width: clamp(7rem, 29vw, 12rem);
  }
}

.video-container {
  flex: 1;
  min-height: 0;
  display: flex;
  align-items: center;
  justify-content: center;
  position: relative;
  overflow: hidden;
}

.video {
  width: 100%;
  height: 100%;
  max-width: 100%;
  max-height: 100%;
  object-fit: contain;
  background: #000;
}

.video::-webkit-media-controls-fullscreen-button {
  display: none;
}

.playback-actions {
  position: absolute;
  inset: auto clamp(1rem, 3vw, 2.5rem) 4.75rem auto;
  z-index: 1;
  display: flex;
  gap: 0.5rem;
}

.playback-overlay {
  pointer-events: none;
  opacity: 0;
  transition: opacity 260ms ease;
}

.playback-overlay.is-visible,
.playback-overlay:has(button:hover, button:focus-visible, label:hover, label:focus-within) {
  opacity: 1;
}

.playback-overlay.is-visible :is(button, label),
.playback-overlay :is(button:hover, button:focus-visible, label:hover, label:focus-within) {
  pointer-events: auto;
}

.playback-overlay :is(button, select):focus-visible {
  outline: 2px solid var(--theatre-accent);
  outline-offset: 3px;
}

.playback-actions button {
  display: flex;
  align-items: center;
  gap: 0.5rem;
  padding: 0.55rem 0.75rem 0.55rem 0.85rem;
  border: 1px solid var(--theatre-border-strong);
  border-radius: 8px;
  background: var(--theatre-panel);
  color: var(--text);
  font-size: 0.875rem;
  font-weight: 600;
  box-shadow: 0 4px 16px #0008;
  cursor: pointer;
}

.playback-actions button:hover,
.playback-actions button:focus-visible {
  border-color: var(--theatre-accent);
  background: var(--theatre-hover);
}

.playback-actions .material-symbols-outlined {
  color: var(--theatre-accent);
  font-size: 22px;
}

.playback-actions .fullscreen-btn {
  padding-inline: 0.55rem;
}

@media (prefers-reduced-motion: reduce) {
  .playback-overlay { transition: none; }
}

.loading-overlay,
.error-overlay {
  position: absolute;
  inset: 0;
  display: flex;
  flex-direction: column;
  align-items: center;
  justify-content: center;
  gap: 1rem;
  background: rgba(0, 0, 0, 0.9);
  color: #fff;
  font-size: 1.1rem;
}

.spinner {
  width: 48px;
  height: 48px;
  border: 4px solid var(--theatre-border);
  border-top-color: var(--theatre-accent);
  border-radius: 50%;
  animation: spin 1s linear infinite;
}

@keyframes spin {
  to {
    transform: rotate(360deg);
  }
}

.buffer-overlay {
  /* Designed to persist for a while: native controls (pause, seek back into
     produced media) must stay reachable underneath it. */
  pointer-events: none;
}

.overlay-detail {
  margin: 0;
  font-size: 0.95rem;
  color: var(--theatre-muted);
}

.overlay-progress {
  width: min(320px, 70%);
  height: 6px;
  border-radius: 3px;
  background: var(--theatre-border);
  overflow: hidden;
}

.overlay-progress-fill {
  height: 100%;
  background: var(--theatre-accent);
  transition: width 0.5s ease;
}

.error-overlay .material-symbols-outlined {
  font-size: 3rem;
  color: #ef4444;
}
</style>
