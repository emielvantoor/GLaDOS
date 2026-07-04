    function toggleTheme() {
        const currentMode = getThemeMode();
        const nextMode = themeModes[(themeModes.indexOf(currentMode) + 1) % themeModes.length];
        saveThemeMode(nextMode);
        applyTheme();
    }

    function getThemeMode() {
        let savedMode = null;

        try {
            savedMode = localStorage.getItem(themeStorageKey);
        } catch {
            savedMode = null;
        }

        return themeModes.includes(savedMode) ? savedMode : "auto";
    }

    function saveThemeMode(mode) {
        try {
            localStorage.setItem(themeStorageKey, mode);
        } catch {
            // Storage can be unavailable when the page is opened directly or privacy settings block it.
        }
    }

    function getAutomaticTheme(now = new Date()) {
        const hour = now.getHours();
        return hour >= 7 && hour < 19 ? "light" : "dark";
    }

    function getTimeTone(now = new Date()) {
        const hour = now.getHours();

        if (hour >= 5 && hour < 10) return "morning";
        if (hour >= 10 && hour < 17) return "day";
        if (hour >= 17 && hour < 21) return "evening";
        return "night";
    }

    function applyTheme() {
        const mode = getThemeMode();
        const activeTheme = mode === "auto" ? getAutomaticTheme() : mode;
        const body = document.body;
        const themeBtn = document.getElementById('themeBtn');

        body.dataset.timeTone = getTimeTone();

        if (activeTheme === "dark") {
            body.setAttribute('data-theme', 'dark');
        } else {
            body.removeAttribute('data-theme');
        }

        if (mode === "auto") {
            themeBtn.innerText = activeTheme === "dark" ? "🌓 Auto: Night" : "🌓 Auto: Day";
        } else if (mode === "dark") {
            themeBtn.innerText = "🌙 Dark Mode";
        } else {
            themeBtn.innerText = "☀️ Light Mode";
        }

        themeBtn.title = "Theme mode: Auto, Light, Dark";
    }
