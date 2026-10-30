(() => {
    const key = "pkrelay.dashboard.preferences";
    let preferences = { theme: "light", motion: true };
    try {
        const stored = JSON.parse(localStorage.getItem(key) || "null");
        preferences.theme = stored?.theme === "dark" ? "dark" : "light";
        preferences.motion = stored?.motion !== false;
    } catch { /* Presentation preferences are optional. */ }
    const apply = () => {
        document.documentElement.dataset.theme = preferences.theme;
        document.documentElement.dataset.motion = preferences.motion ? "on" : "off";
    };
    apply();
    window.pkDashboard = {
        preferences: () => preferences,
        setPreferences: (theme, motion) => {
            preferences = { theme: theme === "dark" ? "dark" : "light", motion: !!motion };
            apply();
            try { localStorage.setItem(key, JSON.stringify(preferences)); } catch { }
        }
    };
})();
