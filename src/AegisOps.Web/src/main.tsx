import { StrictMode } from "react";
import { createRoot } from "react-dom/client";
import { App } from "./App.tsx";
import "./index.css";
import { applyTheme, readThemePreference } from "./theme";

const rootElement = document.getElementById("root");

if (rootElement === null) {
  throw new Error("Root element #root was not found.");
}

// Apply the stored theme before the first render so the page does not flash the other theme.
applyTheme(readThemePreference());

createRoot(rootElement).render(
  <StrictMode>
    <App />
  </StrictMode>,
);
