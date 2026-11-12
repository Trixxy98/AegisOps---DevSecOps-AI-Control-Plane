import { useEffect, useState } from "react";

/** The user's choice. "system" follows prefers-color-scheme live. Only this value is stored, never tokens. */
export type ThemePreference = "system" | "light" | "dark";
export type ResolvedTheme = "light" | "dark";

export const themeStorageKey = "aegisops.theme";
const darkQuery = "(prefers-color-scheme: dark)";

export function readThemePreference(): ThemePreference {
  try {
    const stored = window.localStorage.getItem(themeStorageKey);
    return stored === "light" || stored === "dark" ? stored : "system";
  } catch {
    return "system";
  }
}

function resolveTheme(preference: ThemePreference): ResolvedTheme {
  if (preference !== "system") return preference;
  return window.matchMedia?.(darkQuery).matches ? "dark" : "light";
}

/** Sets data-theme and color-scheme on <html>. Called from main.tsx before the first render (no inline script: CSP). */
export function applyTheme(preference: ThemePreference) {
  const theme = resolveTheme(preference);
  const root = document.documentElement;
  root.dataset.theme = theme;
  root.style.colorScheme = theme;
}

export function useThemePreference(): [ThemePreference, (next: ThemePreference) => void] {
  const [preference, setPreference] = useState<ThemePreference>(readThemePreference);

  useEffect(() => {
    applyTheme(preference);
    try {
      if (preference === "system") window.localStorage.removeItem(themeStorageKey);
      else window.localStorage.setItem(themeStorageKey, preference);
    } catch {
      // Storage can be unavailable (private mode); the theme still applies for this page.
    }
    if (preference !== "system" || !window.matchMedia) return;
    const media = window.matchMedia(darkQuery);
    const onChange = () => applyTheme("system");
    media.addEventListener("change", onChange);
    return () => media.removeEventListener("change", onChange);
  }, [preference]);

  return [preference, setPreference];
}
