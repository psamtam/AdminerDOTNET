"use strict";
(() => {
    const form = document.getElementById("bulk-delete-form");
    if (!form) return;
    const rows = [...document.querySelectorAll(".row-select")];
    const all = document.getElementById("select-page");
    const remove = document.getElementById("delete-selected");
    const counter = document.getElementById("selection-count");
    let pending = false;
    const selected = () => rows.filter(row => row.checked).length;
    const refresh = () => {
        const count = selected();
        all.checked = rows.length > 0 && count === rows.length;
        all.indeterminate = count > 0 && count < rows.length;
        all.disabled = pending || rows.length === 0;
        remove.disabled = pending || count === 0;
        counter.textContent = `${count} selected on this page`;
        rows.forEach(row => row.closest("tr").classList.toggle("selected-row", row.checked));
    };
    all.addEventListener("change", () => { rows.forEach(row => { row.checked = all.checked; }); refresh(); });
    rows.forEach(row => row.addEventListener("change", refresh));
    form.addEventListener("submit", event => {
        const count = selected();
        if (pending || count === 0 || !window.confirm(`Delete ${count} selected rows? This cannot be undone.`)) {
            event.preventDefault();
            return;
        }
        pending = true;
        remove.textContent = "Deleting…";
        refresh();
        // Keep checked inputs enabled so native form submission includes their tokens.
    });
    window.addEventListener("pageshow", () => {
        pending = false;
        rows.forEach(row => { row.checked = false; });
        remove.textContent = "Delete selected";
        refresh();
    });
    refresh();
})();
