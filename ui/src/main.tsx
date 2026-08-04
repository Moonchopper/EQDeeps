import { StrictMode } from "react";
import { createRoot } from "react-dom/client";
import App from "./App";
import { HighlightProvider } from "./highlight";
import "react-grid-layout/css/styles.css";
import "./styles.css";

// The highlight provider sits OUTSIDE App so that pointing at a line re-renders
// the panels that read the hover and nothing else — App holds the sessions, the
// frame and every query, and none of that changes because the mouse moved.
createRoot(document.getElementById("root")!).render(
  <StrictMode>
    <HighlightProvider>
      <App />
    </HighlightProvider>
  </StrictMode>,
);
