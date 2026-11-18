import { serve } from "bun";
import index from "./index.html";

// Bun only serves the React app. All data comes from the .NET SignalR server (../server).
const server = serve({
  port: 3000,
  routes: {
    "/*": index,
  },
  development: process.env.NODE_ENV !== "production" && {
    hmr: true,
    console: true,
  },
});

console.log(`🚀 Client running at ${server.url}`);
