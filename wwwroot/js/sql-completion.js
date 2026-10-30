"use strict";
(() => {
    const form = document.getElementById("sql-form");
    const textarea = document.getElementById("CommandText");
    const engine = window.AdminerSqlCompletion;
    if (!form?.dataset.completionUrl || !textarea || !engine) return;
    const refresh = document.getElementById("refresh-completions");
    const status = document.getElementById("completion-status");
    const popup = document.createElement("div");
    popup.id = "sql-completions";
    popup.className = "sql-completions";
    popup.setAttribute("role", "listbox");
    popup.setAttribute("aria-label", "SQL suggestions");
    popup.hidden = true;
    document.body.append(popup);
    textarea.setAttribute("aria-autocomplete", "list");
    textarea.setAttribute("aria-controls", popup.id);
    textarea.setAttribute("aria-expanded", "false");
    textarea.setAttribute("autocomplete", "off");
    const mirror = document.createElement("div");
    mirror.className = "sql-caret-mirror";
    mirror.setAttribute("aria-hidden", "true");
    document.body.append(mirror);
    let metadata = { objects: [] };
    let completion = null;
    let active = 0;
    let timer;
    let busy = false;
    let accepting = false;
    let request = null;
    let revision = 0;

    function close() {
        popup.hidden = true;
        textarea.setAttribute("aria-expanded", "false");
        textarea.removeAttribute("aria-activedescendant");
    }
    function position() {
        if (popup.hidden) return;
        const style = getComputedStyle(textarea);
        for (const property of ["fontFamily", "fontSize", "fontWeight", "fontStyle", "fontVariant", "lineHeight", "letterSpacing", "textIndent", "textTransform", "tabSize", "paddingTop", "paddingRight", "paddingBottom", "paddingLeft", "borderTopWidth", "borderRightWidth", "borderBottomWidth", "borderLeftWidth", "boxSizing"]) mirror.style[property] = style[property];
        mirror.style.whiteSpace = textarea.wrap === "off" ? "pre" : "pre-wrap";
        mirror.style.width = `${textarea.clientWidth + parseFloat(style.borderLeftWidth) + parseFloat(style.borderRightWidth)}px`;
        mirror.textContent = textarea.value.slice(0, textarea.selectionStart);
        const marker = document.createElement("span");
        marker.textContent = "\u200b";
        mirror.append(marker);
        const caret = marker.getBoundingClientRect();
        const origin = mirror.getBoundingClientRect();
        const bounds = textarea.getBoundingClientRect();
        const line = parseFloat(style.lineHeight) || parseFloat(style.fontSize) * 1.5;
        const top = bounds.top + caret.top - origin.top - textarea.scrollTop;
        const left = bounds.left + caret.left - origin.left - textarea.scrollLeft;
        popup.style.width = `${Math.min(440, Math.max(120, window.innerWidth - 16))}px`;
        popup.style.maxHeight = `${Math.min(240, Math.max(80, window.innerHeight - 16))}px`;
        popup.style.left = `${Math.max(8, Math.min(left, window.innerWidth - popup.offsetWidth - 8))}px`;
        const below = top + line;
        popup.style.top = `${Math.max(8, Math.min(below + popup.offsetHeight > window.innerHeight - 8 ? top - popup.offsetHeight : below, window.innerHeight - popup.offsetHeight - 8))}px`;
    }
    function highlight() {
        [...popup.children].forEach((option, index) => option.setAttribute("aria-selected", String(index === active)));
        const option = popup.children[active];
        if (option) {
            textarea.setAttribute("aria-activedescendant", option.id);
            option.scrollIntoView({ block: "nearest" });
        }
    }
    function accept(index) {
        const item = completion?.items[index];
        if (!item) return;
        accepting = true;
        textarea.setRangeText(item.insert, completion.start, completion.end, "end");
        textarea.dispatchEvent(new Event("input", { bubbles: true }));
        accepting = false;
        textarea.focus();
        close();
        if (item.kind === "schema") show(true);
    }
    function show(force = false) {
        clearTimeout(timer);
        if (busy || document.activeElement !== textarea || textarea.selectionStart !== textarea.selectionEnd) { close(); return; }
        completion = engine.suggest(textarea.value, textarea.selectionStart, metadata, force);
        if (!completion.items.length) { close(); return; }
        active = 0;
        popup.replaceChildren();
        completion.items.forEach((item, index) => {
            const option = document.createElement("div");
            option.id = "sql-completion-" + index;
            option.className = "sql-completion-option";
            option.setAttribute("role", "option");
            const name = document.createElement("strong");
            name.textContent = item.label;
            const detail = document.createElement("small");
            detail.textContent = item.detail;
            option.append(name, detail);
            option.addEventListener("mousedown", event => { event.preventDefault(); accept(index); });
            option.addEventListener("mouseenter", () => { active = index; highlight(); });
            popup.append(option);
        });
        popup.hidden = false;
        textarea.setAttribute("aria-expanded", "true");
        position();
        highlight();
    }
    async function load() {
        request?.abort();
        request = new AbortController();
        const current = ++revision;
        refresh.disabled = true;
        status.textContent = "Loading suggestions…";
        try {
            const response = await fetch(form.dataset.completionUrl, { signal: request.signal, cache: "no-store", headers: { Accept: "application/json" } });
            const payload = await response.json();
            if (!response.ok) throw new Error(payload.error || "Metadata could not be loaded.");
            if (payload.database !== form.elements.db.value || !Array.isArray(payload.objects)) throw new Error("Database context changed. Reload this page.");
            if (current !== revision) return;
            metadata = payload;
            status.textContent = `${metadata.objects.length} tables/views ready`;
        } catch (error) {
            if (error.name === "AbortError" || current !== revision) return;
            metadata = { objects: [] };
            close();
            status.textContent = "Suggestions unavailable. " + error.message + " Use Refresh suggestions to retry.";
        } finally { if (current === revision) refresh.disabled = busy; }
    }
    textarea.addEventListener("input", () => {
        if (accepting) return;
        clearTimeout(timer);
        timer = setTimeout(() => show(), 70);
    });
    textarea.addEventListener("keydown", event => {
        if ((event.ctrlKey || event.metaKey) && event.key === "Enter") { close(); return; }
        if (event.ctrlKey && (event.key === " " || event.code === "Space")) { event.preventDefault(); show(true); return; }
        if (event.key === "Escape") { clearTimeout(timer); if (!popup.hidden) event.preventDefault(); close(); return; }
        if (popup.hidden) return;
        if (event.key === "ArrowDown" || event.key === "ArrowUp") {
            event.preventDefault();
            active = (active + (event.key === "ArrowDown" ? 1 : completion.items.length - 1)) % completion.items.length;
            highlight();
        } else if ((event.key === "Tab" && !event.shiftKey) || event.key === "Enter") { event.preventDefault(); accept(active); }
        else if (event.key === "Tab") close();
        else if (["ArrowLeft", "ArrowRight", "Home", "End"].includes(event.key)) close();
    }, true);
    textarea.addEventListener("blur", close);
    textarea.addEventListener("click", close);
    textarea.addEventListener("scroll", position);
    window.addEventListener("scroll", position, true);
    window.addEventListener("resize", position);
    refresh.addEventListener("click", () => { close(); void load(); });
    form.addEventListener("sql:execution-start", () => { busy = true; clearTimeout(timer); close(); refresh.disabled = true; });
    form.addEventListener("sql:execution-complete", () => { busy = false; void load(); });
    void load();
})();
