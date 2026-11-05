// Deliberately tab-local: browser permission alone never opts a viewer in.
let enabled = false;
let unavailable = false;
const seen = new Set();
const open = new Set();
const available = () => window.isSecureContext && "Notification" in window;
const state = () => unavailable || !available() ? "unavailable" : Notification.permission === "denied" ? "denied" : enabled && Notification.permission === "granted" ? "enabled" : "off";

// Called only by the explicit Enable button, never by initialization or publish.
export async function enable() {
    if (!available()) return "unavailable";
    const permission = Notification.permission === "default" ? await Notification.requestPermission() : Notification.permission;
    enabled = permission === "granted";
    return state();
}

export function disable() {
    enabled = false;
    for (const notification of open) notification.close();
    open.clear();
    return state();
}

export function publish(ids) {
    if (state() !== "enabled") return state();
    for (const id of ids) {
        if (seen.has(id)) continue;
        try {
            // No chat text, labels or credentials appear on a desktop/lock screen.
            const notification = new Notification("CoCern · Waiting on you", {
                body: "Open the dashboard to review an item that needs your attention.",
                tag: id,
                silent: true
            });
            seen.add(id);
            open.add(notification);
            notification.onclose = () => open.delete(notification);
            notification.onclick = () => {
                window.focus();
                const notices = document.querySelector("details.observation-warnings");
                if (notices) { notices.open = true; notices.querySelector("summary")?.focus(); }
                notification.close();
            };
        } catch {
            // A browser can expose the API but refuse desktop notifications.
            disable();
            unavailable = true;
            return "unavailable";
        }
    }
    return state();
}
