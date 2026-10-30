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
    const exportButton = document.getElementById("export-query");
    const exportResult = document.getElementById("export-result");
    const cancel = document.getElementById("cancel-query");
    const status = document.getElementById("query-status");
    let activeId = null;
    form.addEventListener("submit", async event => {
        event.preventDefault();
        if (activeId) return;
        const exporting = event.submitter?.dataset.export === "true";
        const action = exporting ? event.submitter.formAction : form.action;
        activeId = crypto.randomUUID();
        form.elements.ExecutionId.value = activeId;
        run.disabled = true;
        exportButton.disabled = true;
        cancel.disabled = false;
        databaseSelect.disabled = true;
        databaseSwitch.querySelector("button").disabled = true;
        const targetId = form.dataset.browse ? "browse-view" : "execution";
        const originalView = document.getElementById(targetId);
        if (form.dataset.browse) originalView.inert = true;
        const reset = document.getElementById("reset-sql");
        if (reset) reset.inert = true;
        status.textContent = exporting ? "Exporting…" : "Running…";
        exportResult.replaceChildren();
        exportResult.hidden = true;
        form.dispatchEvent(new CustomEvent("sql:execution-start"));
        try {
            const response = await fetch(action, { method: "POST", body: new FormData(form) });
            if (exporting) {
                const payload = await response.json();
                if (!response.ok) throw new Error(payload.error || `Export request failed (${response.status}).`);
                exportResult.hidden = false;
                const summary = document.createElement("p");
                summary.className = payload.error ? "notice error" : "result-summary";
                summary.textContent = payload.error || `${Math.round(payload.milliseconds).toLocaleString()} ms · ${payload.sets.length} result set(s) · ${payload.sets.reduce((sum, set) => sum + set.rows, 0).toLocaleString()} rows exported${payload.affected >= 0 ? ` · ${payload.affected} affected row(s)` : ""}`;
                exportResult.append(summary);
                for (const message of payload.messages || []) {
                    const notice = document.createElement("pre");
                    notice.className = "notice";
                    notice.textContent = message;
                    exportResult.append(notice);
                }
                if (payload.metadataError) {
                    const notice = document.createElement("p");
                    notice.textContent = "Metadata refresh: " + payload.metadataError;
                    exportResult.append(notice);
                } else refreshExportSidebar(payload.objects);
                if (payload.downloadUrl && !payload.error) {
                    const link = document.createElement("a");
                    link.className = "button";
                    link.href = payload.downloadUrl;
                    link.download = "sql-results.zip";
                    link.textContent = "Download CSV ZIP (available for 10 minutes)";
                    exportResult.append(link);
                    link.click(); // Native streamed download; never fetch the archive into a JS Blob.
                } else if (!payload.error) {
                    const notice = document.createElement("p");
                    notice.textContent = "No result sets were returned. No ZIP was created.";
                    exportResult.append(notice);
                }
                status.textContent = payload.error ? "Export failed" : "Export finished";
                return;
            }
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
            activeId = null; run.disabled = false; exportButton.disabled = false; cancel.disabled = true;
            databaseSelect.disabled = false;
            databaseSwitch.querySelector("button").disabled = false;
            originalView.inert = false;
            if (reset) reset.inert = false;
            form.dispatchEvent(new CustomEvent("sql:execution-complete"));
        }
    });
    function refreshExportSidebar(objects) {
        const list = document.querySelector(".objects");
        const selectedId = Number(form.elements.Id?.value);
        list.replaceChildren();
        for (const object of objects || []) {
            const link = document.createElement("a");
            const url = new URL("/Browse", window.location.origin);
            url.searchParams.set("db", form.elements.db.value);
            url.searchParams.set("id", object.id);
            link.href = url;
            if (object.id === selectedId) link.className = "selected";
            const icon = document.createElement("span");
            icon.className = "object-icon";
            icon.textContent = object.isView ? "V" : "T";
            link.append(icon, `${object.schema}.${object.name}`);
            list.append(link);
        }
        document.querySelector(".sidebar-heading span").textContent = (objects || []).length;
        document.getElementById("object-search").value = "";
    }
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
