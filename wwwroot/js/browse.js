"use strict";
// The browser edits only a filter expression. SQL Server always filters and sorts the records.
(() => {
    const host = document.getElementById("filter-builder");
    if (!host) return;
    const form = document.getElementById("filters-form");
    const columns = JSON.parse(host.dataset.columns);
    const labels = { eq: "equals", ne: "does not equal", lt: "less than", le: "less than or equal", gt: "greater than", ge: "greater than or equal", contains: "contains", notcontains: "does not contain", starts: "starts with", ends: "ends with", null: "is NULL", notnull: "is not NULL", empty: "is empty text", notempty: "is not empty text" };
    let root;
    try { root = JSON.parse(host.dataset.state || "null"); } catch { root = null; }
    if (!root) root = { logic: "and", children: [] };
    else if (!Array.isArray(root.children)) root = { logic: "and", children: [root] };
    const element = (tag, className, text) => {
        const node = document.createElement(tag);
        if (className) node.className = className;
        if (text) node.textContent = text;
        return node;
    };
    function button(text, action) {
        const node = element("button", "", text);
        node.type = "button";
        node.addEventListener("click", action);
        return node;
    }
    function option(select, value, text) {
        const item = element("option", "", text);
        item.value = value;
        select.append(item);
    }
    function condition(node, parent, index) {
        const row = element("div", "filter-condition");
        const field = element("select");
        field.setAttribute("aria-label", "Filter column");
        columns.forEach(column => option(field, column.id, `${column.name} · ${column.type}`));
        field.value = node.column;
        if (field.selectedIndex < 0) field.selectedIndex = 0;
        node.column = Number(field.value);
        const operator = element("select");
        operator.setAttribute("aria-label", "Filter operator");
        const input = element("input");
        input.type = "text";
        input.placeholder = "Value (ISO dates, 0/1 bits, hex binary)";
        input.setAttribute("aria-label", "Filter value");
        input.value = node.value ?? "";
        input.addEventListener("input", () => { node.value = input.value; });
        const unary = () => {
            const noValue = ["null", "notnull", "empty", "notempty"].includes(node.operator);
            input.disabled = noValue;
            input.hidden = noValue;
            if (noValue) delete node.value;
            else node.value = input.value;
        };
        const operators = () => {
            operator.replaceChildren();
            const column = columns.find(c => c.id === node.column);
            (column?.operators || []).forEach(op => option(operator, op, labels[op]));
            operator.value = node.operator;
            if (operator.selectedIndex < 0) operator.selectedIndex = 0;
            node.operator = operator.value;
            unary();
        };
        field.addEventListener("change", () => { node.column = Number(field.value); operators(); });
        operator.addEventListener("change", () => { node.operator = operator.value; unary(); });
        operators();
        row.append(field, operator, input, button("Remove", () => { parent.children.splice(index, 1); render(); }));
        return row;
    }
    function group(node, parent, index) {
        const box = element("fieldset", "filter-group");
        const legend = element("legend", "", "Match");
        const logic = element("select");
        logic.setAttribute("aria-label", "Group logic");
        option(logic, "and", "All (AND)");
        option(logic, "or", "Any (OR)");
        logic.value = node.logic;
        logic.addEventListener("change", () => { node.logic = logic.value; });
        legend.append(logic);
        box.append(legend);
        const children = element("div", "filter-children");
        node.children.forEach((child, i) => children.append(Array.isArray(child.children) ? group(child, node, i) : condition(child, node, i)));
        if (node.children.length === 0) children.append(element("p", "muted", "No conditions. Add a condition or a group."));
        box.append(children);
        const actions = element("div", "filter-actions");
        actions.append(button("+ Add condition", () => {
            if (!columns.length) return;
            node.children.push({ column: columns[0].id, operator: columns[0].operators[0], value: "" });
            render();
        }), button("+ Add group", () => { node.children.push({ logic: "and", children: [] }); render(); }));
        if (parent) actions.append(button("Remove group", () => { parent.children.splice(index, 1); render(); }));
        box.append(actions);
        return box;
    }
    function render() { host.replaceChildren(group(root)); }
    form.addEventListener("submit", () => { document.getElementById("filters-state").value = JSON.stringify(root); });
    render();
})();
