(() => {
    // The appearance follows the system until the viewer picks one. Motion always follows the system (prefers-reduced-motion).
    const key = "cocern.dashboard.preferences";
    const system = () => matchMedia("(prefers-color-scheme: dark)").matches ? "dark" : "light";
    let chosen = null;
    try {
        const stored = JSON.parse(localStorage.getItem(key) || "null");
        if (stored?.theme === "dark" || stored?.theme === "light") chosen = stored.theme;
    } catch { /* Preferences are optional. */ }
    const theme = () => chosen ?? system();
    const apply = () => {
        document.documentElement.dataset.theme = theme();
        document.querySelector('meta[name="theme-color"]')?.setAttribute("content", theme() === "dark" ? "#0c2230" : "#edf3f4");
    };
    apply();
    matchMedia("(prefers-color-scheme: dark)").addEventListener?.("change", () => { if (!chosen) apply(); });
    window.pkDashboard = {
        preferences: () => ({ theme: theme() }),
        setPreferences: (value) => {
            chosen = value === "dark" ? "dark" : "light";
            apply();
            try { localStorage.setItem(key, JSON.stringify({ theme: chosen })); } catch { }
        }
    };
})();
