import { app, BrowserWindow, dialog, protocol, session } from "electron";
import { spawn } from "node:child_process";
import { randomBytes } from "node:crypto";
import { createInterface } from "node:readline";
import { fileURLToPath } from "node:url";
import { join } from "node:path";

const scheme = "home-theatre";
const origin = `${scheme}://app`;
const root = fileURLToPath(new URL("../", import.meta.url));
let backend;
let window;
let quitting = false;

app.setName("Home Theatre");
protocol.registerSchemesAsPrivileged([{
  scheme,
  privileges: { standard: true, secure: true, supportFetchAPI: true, corsEnabled: true, stream: true },
}]);

function startBackend(token) {
  backend = spawn("dotnet", [join(root, "backend/bin/Release/net10.0/HomeTheatre.Backend.dll")], {
    cwd: root,
    env: {
      ...process.env,
      HOME_THEATRE_TOKEN: token,
      HOME_THEATRE_RENDERER: join(root, "dist"),
      HOME_THEATRE_Video__StreamTempRoot:
        process.env.HOME_THEATRE_Video__StreamTempRoot ?? join(app.getPath("sessionData"), "streams"),
    },
    stdio: ["pipe", "pipe", "inherit"],
    windowsHide: true,
  });
  backend.stdin.on("error", () => {});

  return new Promise((resolve, reject) => {
    let ready = false;
    const timeout = setTimeout(() => reject(new Error("The .NET backend did not start within 15 seconds.")), 15_000);
    backend.on("error", error => {
      clearTimeout(timeout);
      reject(error);
    });
    backend.on("exit", (code, signal) => {
      clearTimeout(timeout);
      const error = new Error(`The .NET backend exited (${signal ?? code}).`);
      if (!ready) reject(error);
      else if (!quitting) {
        dialog.showErrorBox("Home Theatre backend stopped", error.message);
        app.quit();
      }
    });
    createInterface({ input: backend.stdout }).on("line", line => {
      const match = /^HOME_THEATRE_READY (http:\/\/127\.0\.0\.1:\d+)$/.exec(line);
      if (!match) {
        console.log(line);
        return;
      }
      clearTimeout(timeout);
      ready = true;
      resolve(match[1]);
    });
  });
}

async function launch() {
  const token = randomBytes(32).toString("hex");
  const address = await startBackend(token);

  protocol.handle(scheme, async request => {
    const url = new URL(request.url);
    if (url.host !== "app" || url.username || url.password) return new Response(null, { status: 403 });
    const headers = new Headers({ "X-Home-Theatre-Token": token });
    for (const name of ["content-type", "range", "if-none-match", "if-modified-since"]) {
      const value = request.headers.get(name);
      if (value !== null) headers.set(name, value);
    }
    try {
      return await fetch(address + url.pathname + url.search, {
        method: request.method,
        headers,
        body: request.body,
        duplex: "half",
        redirect: "error",
        signal: request.signal,
      });
    } catch (error) {
      if (!request.signal.aborted) console.error("Home Theatre backend request failed", error);
      return new Response(null, { status: 503 });
    }
  });

  const canEnterFullscreen = (contents, permission, isMainFrame, url) =>
    permission === "fullscreen" && contents === window?.webContents && isMainFrame &&
    typeof url === "string" && (url === origin || url.startsWith(origin + "/"));
  session.defaultSession.setPermissionCheckHandler((contents, permission, requestingOrigin, details) =>
    canEnterFullscreen(contents, permission, details.isMainFrame, details.requestingUrl ?? requestingOrigin));
  session.defaultSession.setPermissionRequestHandler((contents, permission, callback, details) =>
    callback(canEnterFullscreen(contents, permission, details.isMainFrame, details.requestingUrl)));
  window = new BrowserWindow({
    title: "Home Theatre",
    icon: join(root, "desktop/home-theatre.png"),
    width: 1440,
    height: 900,
    minWidth: 640,
    minHeight: 480,
    backgroundColor: '#141414',
    ...(process.platform === 'linux' ? {
      titleBarStyle: 'hidden',
      titleBarOverlay: { color: '#141414', symbolColor: '#eeeeee', height: 32 },
    } : {}),
    autoHideMenuBar: true,
    webPreferences: { contextIsolation: true, sandbox: true, nodeIntegration: false },
  });
  window.removeMenu();
  window.webContents.setWindowOpenHandler(() => ({ action: "deny" }));
  window.webContents.on("will-navigate", event => event.preventDefault());
  window.webContents.on("will-frame-navigate", event => event.preventDefault());
  window.on("closed", () => { window = undefined; });
  await window.loadURL(origin + "/");
}

if (!app.requestSingleInstanceLock()) {
  app.quit();
} else {
  app.on("second-instance", () => {
    if (window?.isMinimized()) window.restore();
    window?.focus();
  });
  app.on("window-all-closed", () => app.quit());
  app.on("will-quit", () => {
    quitting = true;
    backend?.stdin.end();
  });
  app.whenReady().then(launch).catch(error => {
    dialog.showErrorBox("Home Theatre could not start", error.message);
    app.quit();
  });
}
