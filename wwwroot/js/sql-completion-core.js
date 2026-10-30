"use strict";
// Pure SQL completion helpers. No DOM, network, execution, or dependency on an editor library.
(() => {
    const keywords = ["SELECT", "FROM", "WHERE", "JOIN", "LEFT", "RIGHT", "INNER", "OUTER", "FULL", "CROSS", "ON", "AS", "AND", "OR", "NOT", "NULL", "IS", "IN", "LIKE", "BETWEEN", "ORDER BY", "GROUP BY", "HAVING", "ASC", "DESC", "DISTINCT", "TOP", "INSERT INTO", "UPDATE", "DELETE FROM", "SET", "VALUES", "COUNT", "SUM", "AVG", "MIN", "MAX", "CASE", "WHEN", "THEN", "ELSE", "END", "OFFSET", "FETCH", "UNION", "WITH"];
    const reserved = new Set(keywords.flatMap(word => word.split(" ")).concat(["BY", "INTO", "LIMIT", "OPTION", "EXCEPT", "INTERSECT", "APPLY", "USING"]));
    const identifierChar = char => !!char && /[\p{L}\p{N}_@$#]/u.test(char);
    const lower = text => text.toLowerCase();
    const quote = text => "[" + text.replace(/\]/g, "]]") + "]";
    function tokenize(sql) {
        const result = [];
        let i = 0;
        while (i < sql.length) {
            if (/\s/.test(sql[i])) { i++; continue; }
            const start = i;
            if (sql.startsWith("--", i)) {
                while (i < sql.length && sql[i] !== "\n" && sql[i] !== "\r") i++;
                result.push({ kind: "line-comment", start, end: i, closed: true });
            } else if (sql.startsWith("/*", i)) {
                let nesting = 1;
                i += 2;
                while (i < sql.length && nesting > 0) {
                    if (sql.startsWith("/*", i)) { nesting++; i += 2; }
                    else if (sql.startsWith("*/", i)) { nesting--; i += 2; }
                    else i++;
                }
                result.push({ kind: "comment", start, end: i, closed: nesting === 0 });
            } else if (["'", "[", '"'].includes(sql[i])) {
                const opening = sql[i++];
                const ending = opening === "[" ? "]" : opening;
                let value = "";
                let closed = false;
                while (i < sql.length) {
                    if (sql[i] === ending) {
                        if (sql[i + 1] === ending) { value += ending; i += 2; }
                        else { i++; closed = true; break; }
                    } else value += sql[i++];
                }
                result.push({ kind: opening === "'" ? "string" : "identifier", start, end: i, value, quoted: opening !== "'", opening, closed });
            } else if (identifierChar(sql[i])) {
                while (i < sql.length && identifierChar(sql[i])) i++;
                result.push({ kind: "identifier", start, end: i, value: sql.slice(start, i), quoted: false, closed: true });
            } else {
                result.push({ kind: "punctuation", start, end: ++i, value: sql[start], closed: true });
            }
        }
        return result;
    }
    const isWord = (token, word) => token?.kind === "identifier" && !token.quoted && token.value.toUpperCase() === word;
    const punct = (token, value) => token?.kind === "punctuation" && token.value === value;
    const isName = token => token?.kind === "identifier" && (token.quoted || !reserved.has(token.value.toUpperCase()));
    function resolveObject(parts, objects, defaultSchema) {
        if (parts.length < 1 || parts.length > 2) return null;
        const matches = objects.filter(object => lower(object.name) === lower(parts.at(-1)) && (parts.length === 1 || lower(object.schema) === lower(parts[0])));
        if (parts.length === 1) return matches.find(object => lower(object.schema) === lower(defaultSchema)) || matches.find(object => object.schema === "dbo") || null;
        if (matches.length === 1) return matches[0];
        return null;
    }
    function references(tokens, objects, depth, defaultSchema) {
        const refs = [];
        for (let i = 0; i < tokens.length; i++) {
            if (tokens[i].depth !== depth || !["FROM", "JOIN"].some(word => isWord(tokens[i], word))) continue;
            let next = i + 1;
            do {
                if (!isName(tokens[next]) || tokens[next].depth !== depth) break;
                const parts = [tokens[next++].value];
                while (punct(tokens[next], ".") && isName(tokens[next + 1])) { parts.push(tokens[next + 1].value); next += 2; }
                let alias = null;
                if (isWord(tokens[next], "AS")) { next++; if (isName(tokens[next])) alias = tokens[next++].value; }
                else if (isName(tokens[next])) alias = tokens[next++].value;
                const object = resolveObject(parts, objects, defaultSchema);
                if (object) refs.push({ object, alias, qualifier: alias || object.name });
                if (!punct(tokens[next], ",")) break;
                next++;
            } while (next < tokens.length);
        }
        return refs;
    }
    function suggest(sql, caret, metadata, force = false) {
        caret = Math.max(0, Math.min(caret, sql.length));
        const all = tokenize(sql);
        if (all.some(token => ["string", "comment", "line-comment"].includes(token.kind) && caret > token.start &&
            (caret < token.end || (caret === token.end && (!token.closed || token.kind === "line-comment"))))) return { items: [], start: caret, end: caret };
        const statementStart = all.filter(token => punct(token, ";") && token.end <= caret).at(-1)?.end || 0;
        const statementEnd = all.find(token => punct(token, ";") && token.start >= caret)?.start ?? sql.length;
        const tokens = all.filter(token => token.start >= statementStart && token.end <= statementEnd && !["comment", "line-comment"].includes(token.kind));
        let nesting = 0;
        for (const token of tokens) {
            if (punct(token, ")")) nesting--;
            token.depth = nesting;
            if (punct(token, "(")) nesting++;
        }
        const current = tokens.find(token => token.kind === "identifier" && token.start <= caret && caret <= token.end);
        const start = current?.start ?? caret;
        const end = current?.end ?? caret;
        let prefix = current ? sql.slice(current.start + (current.quoted ? 1 : 0), caret) : "";
        if (current?.quoted) {
            const ending = current.opening === "[" ? "]" : '"';
            if (current.closed && caret === current.end) prefix = prefix.slice(0, -1);
            prefix = prefix.split(ending + ending).join(ending);
        }
        const depth = current?.depth ?? (() => { let value = 0; for (const t of tokens.filter(t => t.end <= caret)) { if (punct(t, "(")) value++; if (punct(t, ")")) value--; } return value; })();
        const before = tokens.filter(token => token.end <= start);
        let index = before.length - 1;
        const qualifiers = [];
        while (punct(before[index], ".") && before[index - 1]?.kind === "identifier") { qualifiers.unshift(before[index - 1].value); index -= 2; }
        const context = before[index];
        const clause = before.slice(0, index + 1).filter(token => token.depth === depth && ["FROM", "JOIN", "SELECT", "WHERE", "ON", "ORDER", "GROUP", "HAVING"].some(word => isWord(token, word))).at(-1);
        const tableContext = isWord(context, "FROM") || isWord(context, "JOIN") || (punct(context, ",") && (isWord(clause, "FROM") || isWord(clause, "JOIN")));
        if (!force && !prefix && !qualifiers.length && !tableContext) return { items: [], start, end };
        const objects = metadata?.objects || [];
        const items = [];
        const matches = name => lower(name).startsWith(lower(prefix));
        const followedByDot = !!current && punct(tokens.find(token => token.start >= end), ".");
        if (tableContext) {
            if (qualifiers.length <= 1) {
                for (const object of objects) {
                    if (followedByDot && !qualifiers.length) continue;
                    if (qualifiers.length && lower(object.schema) !== lower(qualifiers[0])) continue;
                    if (matches(object.name)) items.push({ label: object.schema + "." + object.name, insert: qualifiers.length ? quote(object.name) : quote(object.schema) + "." + quote(object.name), detail: object.isView ? "View" : "Table", kind: "object" });
                }
                if (!qualifiers.length) for (const schema of new Set(objects.map(object => object.schema)))
                    if (matches(schema)) items.push({ label: schema, insert: quote(schema) + (followedByDot ? "" : "."), detail: "Schema", kind: "schema" });
            }
        } else {
            const refs = references(tokens, objects, depth, metadata?.defaultSchema || "dbo");
            if (followedByDot) {
                for (const ref of refs) {
                    if (qualifiers.length === 0 && matches(ref.qualifier)) items.push({ label: ref.qualifier, insert: quote(ref.qualifier), detail: "Table reference", kind: "alias" });
                    else if (qualifiers.length === 1 && !ref.alias && lower(ref.object.schema) === lower(qualifiers[0]) && matches(ref.object.name))
                        items.push({ label: ref.object.name, insert: quote(ref.object.name), detail: "Table reference", kind: "alias" });
                }
                return { items: items.slice(0,20), start, end };
            }
            const eligible = qualifiers.length ? refs.filter(ref =>
                (qualifiers.length === 1 && lower(ref.qualifier) === lower(qualifiers[0])) ||
                (qualifiers.length === 2 && !ref.alias && lower(ref.object.schema) === lower(qualifiers[0]) && lower(ref.object.name) === lower(qualifiers[1]))) : refs;
            const counts = new Map();
            for (const ref of eligible) for (const column of ref.object.columns) counts.set(lower(column.name), (counts.get(lower(column.name)) || 0) + 1);
            for (const ref of eligible) for (const column of ref.object.columns) {
                if (!matches(column.name)) continue;
                const ambiguous = !qualifiers.length && counts.get(lower(column.name)) > 1;
                items.push({ label: ambiguous ? ref.qualifier + "." + column.name : column.name,
                    insert: ambiguous ? quote(ref.qualifier) + "." + quote(column.name) : quote(column.name),
                    detail: `${ref.qualifier} · ${column.type}`, kind: "column" });
            }
            if (!qualifiers.length) for (const keyword of keywords) if (matches(keyword)) items.push({ label: keyword, insert: keyword, detail: "SQL keyword", kind: "keyword" });
        }
        const unique = [...new Map(items.map(item => [item.label + "|" + item.insert + "|" + item.detail, item])).values()];
        unique.sort((a, b) => (a.kind === "keyword") - (b.kind === "keyword") || a.label.localeCompare(b.label));
        return { items: unique.slice(0, 20), start, end };
    }
    const api = Object.freeze({ tokenize, suggest, quote });
    if (typeof module !== "undefined" && module.exports) module.exports = api;
    else window.AdminerSqlCompletion = api;
})();
