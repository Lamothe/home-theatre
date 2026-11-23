import { createApp } from "vue";
import { createRouter, createWebHashHistory, RouterView } from "vue-router";
import "material-symbols/outlined.css";
import "./style.css";
import { importAtomicHistory } from "./services/history";
import VideoPage from "./pages/VideoPage.vue";

const router = createRouter({
  history: createWebHashHistory(),
  routes: [
    { path: "/", redirect: "/video" },
    { path: "/video/:pathMatch(.*)*", component: VideoPage },
  ],
});

try {
  await importAtomicHistory();
} catch (error) {
  console.error("Could not import Atomic watch history", error);
}
createApp(RouterView).use(router).mount("#app");
