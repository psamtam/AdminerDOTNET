"use strict";
const databaseSwitch = document.getElementById("database-switch");
const databaseSelect = document.getElementById("database-select");
const recordForm = document.getElementById("record-form");
const recordInitial = recordForm ? new URLSearchParams(new FormData(recordForm)).toString() : null;
databaseSwitch?.addEventListener("submit", event => {
    const changedRecord = recordForm && new URLSearchParams(new FormData(recordForm)).toString() !== recordInitial;
    const sql = document.getElementById("CommandText");
    if (databaseSelect.disabled || ((changedRecord || sql?.value.trim()) &&
        !window.confirm("Switch database and discard the current SQL, results, or unsaved record changes?"))) {
        event.preventDefault();
        databaseSelect.value = databaseSelect.dataset.current;
    }
});
databaseSelect?.addEventListener("change", () => databaseSwitch.requestSubmit());
document.getElementById("object-search")?.addEventListener("input", event => {
    const search = event.target.value.toLowerCase();
    document.querySelectorAll(".objects a").forEach(link => { link.hidden = !link.textContent.toLowerCase().includes(search); });
});
document.addEventListener("click", event => {
    const button = event.target.closest("[data-confirm]");
    if (button && !window.confirm(button.dataset.confirm)) event.preventDefault();
});
document.querySelectorAll(".field-mode").forEach(select => {
    const update = () => { select.closest("tr").querySelector("textarea").readOnly = select.value !== "value"; };
    select.addEventListener("change", update);
    update();
});
const form = document.getElementById("sql-form");
if (form) {
    const run = document.getElementById("run-query");
    const cancel = document.getElementById("cancel-query");
    const status = document.getElementById("query-status");
    let activeId = null;
    form.addEventListener("submit", async event => {
        event.preventDefault();
        if (activeId) return;
        activeId = crypto.randomUUID();
        form.elements.ExecutionId.value = activeId;
        run.disabled = true;
        cancel.disabled = false;
        databaseSelect.disabled = true;
        databaseSwitch.querySelector("button").disabled = true;
        const targetId = form.dataset.browse ? "browse-view" : "execution";
        const originalView = document.getElementById(targetId);
        if (form.dataset.browse) originalView.inert = true;
        const reset = document.getElementById("reset-sql");
        if (reset) reset.inert = true;
        status.textContent = "Running…";
        try {
            const response = await fetch(form.action, { method: "POST", body: new FormData(form) });
            if (!response.ok) throw new Error(`Request failed (${response.status}).`);
            const page = new DOMParser().parseFromString(await response.text(), "text/html");
            const result = page.getElementById(targetId);
            if (!result) throw new Error(page.querySelector("main > .notice.error")?.textContent || "The server could not return query results. Reload the page.");
            document.getElementById(targetId).replaceWith(result);
            for (const selector of [".objects", ".sidebar-heading"]) {
                const updated = page.querySelector(selector);
                if (updated) document.querySelector(selector).replaceWith(updated);
            }
            document.getElementById("object-search").value = "";
            const error = page.querySelector("main > .notice.error");
            if (error) result.prepend(error);
            status.textContent = "Finished";
        } catch (error) { status.textContent = error.message; }
        finally {
            activeId = null; run.disabled = false; cancel.disabled = true;
            databaseSelect.disabled = false;
            databaseSwitch.querySelector("button").disabled = false;
            originalView.inert = false;
            if (reset) reset.inert = false;
        }
    });
    cancel.addEventListener("click", async () => {
        if (!activeId) return;
        const data = new FormData();
        data.set("executionId", activeId);
        data.set("db", form.elements.db.value);
        data.set("__RequestVerificationToken", form.elements.__RequestVerificationToken.value);
        cancel.disabled = true;
        status.textContent = "Cancelling…";
        try {
            const cancelUrl = new URL("/Sql", window.location.origin);
            cancelUrl.searchParams.set("handler", "Cancel");
            cancelUrl.searchParams.set("db", form.elements.db.value);
            const response = await fetch(cancelUrl, { method: "POST", body: data });
            if (!response.ok) throw new Error("Cancel request failed.");
            const result = await response.json();
            if (!result.cancelled && activeId) {
                status.textContent = "Execution has not started or already finished. Retry cancellation if still running.";
                cancel.disabled = false;
            }
        } catch (error) { status.textContent = error.message; cancel.disabled = false; }
    });
    form.elements.CommandText.addEventListener("keydown", event => {
        if ((event.ctrlKey || event.metaKey) && event.key === "Enter") { event.preventDefault(); form.requestSubmit(); }
    });
}
