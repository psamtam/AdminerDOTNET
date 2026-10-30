"use strict";
const assert = require("node:assert/strict");
const engine = require("../wwwroot/js/sql-completion-core.js");
let checks = 0;
function test(name, action) { action(); checks++; console.log("PASS: " + name); }
const metadata = { database: "SimulatedDatabase", defaultSchema: "dbo", objects: [
    {schema:"dbo",name:"table1",isView:false,columns:[{name:"Id",type:"int"},{name:"Name",type:"nvarchar(40)"},{name:"Created",type:"datetime2(7)"}]},
    {schema:"dbo",name:"table2",isView:false,columns:[{name:"Id",type:"int"},{name:"Description",type:"nvarchar(max)"}]},
    {schema:"dbo",name:"thatTable",isView:true,columns:[{name:"ColumnName",type:"int"}]},
    {schema:"other",name:"table1",isView:false,columns:[{name:"OtherColumn",type:"bit"}]},
    {schema:"dbo",name:"Order ] data",isView:false,columns:[{name:"Odd ] column",type:"nchar(10)"}]}
]};
function suggest(marked, force=true, data=metadata) {
    const caret = marked.indexOf("|"); assert(caret>=0);
    const sql=marked.replace("|","");
    const result=engine.suggest(sql,caret,data,force);
    return {...result, sql, applied:item=>sql.slice(0,result.start)+item.insert+sql.slice(result.end)};
}
const labels = result=>result.items.map(item=>item.label);
test("FROM prefixes suggest tables and views",()=>assert.deepEqual(labels(suggest("select * from t|")),["dbo.table1","dbo.table2","dbo.thatTable","other.table1"]));
test("JOIN prefix",()=>assert(labels(suggest("SELECT * FROM dbo.table1 t JOIN ta|")).includes("dbo.table2")));
test("Schema-qualified table prefix preserves schema",()=>{const r=suggest("SELECT * FROM dbo.ta|");assert(r.items.every(i=>!i.label.startsWith("other.")));assert.equal(r.applied(r.items[0]),"SELECT * FROM dbo.[table1]");});
test("Bracketed schema qualifier",()=>assert(suggest("SELECT * FROM [dbo].ta|").items.every(i=>i.insert.startsWith("["))));
test("Empty FROM prefix shows objects",()=>assert(suggest("SELECT * FROM |",false).items.some(i=>i.kind==="object")));
test("Schema names can be completed",()=>assert(suggest("SELECT * FROM db|").items.some(i=>i.insert==="[dbo].")));
test("Do not suggest other-database metadata",()=>assert.equal(suggest("SELECT * FROM OtherDb.dbo.t|").items.length,0));
test("Case-insensitive name prefixes",()=>assert(suggest("select * from TAB|").items.some(i=>i.label==="dbo.table1")));
test("Table name requiring escaping",()=>{const r=suggest("SELECT * FROM Or|");assert.equal(r.items[0].insert,"[dbo].[Order ]] data]");});
test("Complete quoted table in middle of token",()=>{const r=suggest("SELECT * FROM [dbo].[ta|ble1] WHERE 1=1");assert.equal(r.applied(r.items.find(i=>i.label==="dbo.table2")),"SELECT * FROM [dbo].[table2] WHERE 1=1");});
test("Explicit alias column",()=>assert.equal(suggest("SELECT t.Na| FROM dbo.table1 AS t").items[0].insert,"[Name]"));
test("Implicit alias column",()=>assert.equal(suggest("SELECT t.Cr| FROM dbo.table1 t").items[0].label,"Created"));
test("Unqualified column resolves FROM following caret",()=>assert.equal(suggest("SELECT Na| FROM dbo.table1 t").items[0].insert,"[Name]"));
test("Dot opens columns automatically",()=>assert.equal(suggest("SELECT t.| FROM dbo.table1 t",false).items.length,3));
test("Aliased name does not resolve as original table name",()=>assert.equal(suggest("SELECT table1.Na| FROM dbo.table1 t").items.length,0));
test("Unaliased table qualifier",()=>assert.equal(suggest("SELECT table1.Na| FROM dbo.table1").items[0].label,"Name"));
test("Schema and table-qualified column",()=>assert.equal(suggest("SELECT dbo.table1.Na| FROM dbo.table1").items[0].label,"Name"));
test("JOIN aliases and ambiguous columns",()=>assert.deepEqual(suggest("SELECT I| FROM dbo.table1 t JOIN dbo.table2 u ON t.Id=u.Id").items.filter(i=>i.kind==="column").map(i=>i.insert),["[t].[Id]","[u].[Id]"]));
test("Qualified join column",()=>assert.equal(suggest("SELECT u.De| FROM dbo.table1 t LEFT JOIN dbo.table2 AS u ON t.Id=u.Id").items[0].label,"Description"));
test("Comma table list resolves second alias",()=>assert.equal(suggest("SELECT b.De| FROM dbo.table1 a, dbo.table2 b").items[0].label,"Description"));
test("Comma FROM context offers tables",()=>assert(suggest("SELECT * FROM dbo.table1, ta|").items.some(i=>i.kind==="object")));
test("Default schema resolves duplicate table names",()=>assert.equal(suggest("SELECT Na| FROM table1").items[0].label,"Name"));
test("Actual caller default schema is respected",()=>assert.equal(suggest("SELECT OtherC| FROM table1",true,{...metadata,defaultSchema:"other"}).items[0].label,"OtherColumn"));
test("Explicit schema overrides default schema",()=>assert.equal(suggest("SELECT OtherC| FROM other.table1").items[0].label,"OtherColumn"));
test("Unknown alias produces no guessed columns",()=>assert.equal(suggest("SELECT missing.Na| FROM dbo.table1 t").items.length,0));
test("Quoted column replacement includes old closing bracket",()=>{const r=suggest("SELECT t.[Na|me] FROM dbo.table1 t");assert.equal(r.applied(r.items[0]),"SELECT t.[Name] FROM dbo.table1 t");});
test("Double-quoted identifiers",()=>assert.equal(suggest('SELECT "t"."Na|me" FROM "dbo"."table1" AS "t"').items[0].label,"Name"));
test("Escape unusual column names",()=>assert.equal(suggest("SELECT q.Od| FROM dbo.[Order ]] data] q").items[0].insert,"[Odd ]] column]"));
test("Middle-of-statement replacement preserves suffix",()=>{const r=suggest("SELECT t.Na|me, t.Id FROM dbo.table1 t WHERE t.Id=1");assert.equal(r.applied(r.items[0]),"SELECT t.[Name], t.Id FROM dbo.table1 t WHERE t.Id=1");});
test("String literals suppress suggestions",()=>assert.equal(suggest("SELECT 'FROM t.Na|me' FROM dbo.table1 t").items.length,0));
test("Escaped string quote suppresses suggestions",()=>assert.equal(suggest("SELECT N'it''s Na|me' FROM dbo.table1 t").items.length,0));
test("Unterminated literal suppresses suggestions",()=>assert.equal(suggest("SELECT N'Na|").items.length,0));
test("Line comment suppresses suggestions",()=>assert.equal(suggest("SELECT * FROM dbo.table1 -- t.Na|").items.length,0));
test("Block comment suppresses suggestions",()=>assert.equal(suggest("SELECT /* Na| */ * FROM dbo.table1").items.length,0));
test("Nested block comment suppresses suggestions",()=>assert.equal(suggest("SELECT /* outer /* inner */ Na| */ * FROM dbo.table1").items.length,0));
test("Semicolon in literal does not split statement",()=>assert.equal(suggest("SELECT ';', t.Na| FROM dbo.table1 t").items[0].label,"Name"));
test("Parenthesis in literal does not change scope",()=>assert.equal(suggest("SELECT '(', t.Na| FROM dbo.table1 t").items[0].label,"Name"));
test("Semicolon separates alias scopes",()=>assert.equal(suggest("SELECT * FROM dbo.table1 t; SELECT t.Na| FROM dbo.table2 u").items.length,0));
test("DECLARE preceding SELECT does not lose context",()=>assert.equal(suggest("DECLARE @x int=1; SELECT t.Na| FROM dbo.table1 t WHERE Id=@x").items[0].label,"Name"));
test("Commented table reference is ignored",()=>assert.equal(suggest("SELECT t.Na| /* FROM dbo.table1 t */ FROM dbo.table2 u").items.length,0));
test("Keywords without metadata",()=>assert(suggest("SEL|",true,{objects:[]}).items.some(i=>i.label==="SELECT")));
test("Automatic empty noncontext prefix stays closed",()=>assert.equal(suggest("SELECT |",false).items.length,0));
test("Manual invocation opens columns at empty prefix",()=>assert(suggest("SELECT | FROM dbo.table1").items.some(i=>i.label==="Name")));
test("Completion metadata is isolated between calls",()=>assert(!suggest("SELECT * FROM t|",true,{objects:[{schema:"dbo",name:"totallyDifferent",columns:[]}]}).items.some(i=>i.label==="dbo.table1")));
test("At most 20 suggestions",()=>assert.equal(suggest("SELECT * FROM t|",true,{objects:Array.from({length:60},(_,i)=>({schema:"dbo",name:"table"+i,columns:[]}))}).items.length,20));
test("Editing an existing schema preserves its one dot",()=>{const r=suggest("SELECT * FROM db|o.table1");assert.equal(r.applied(r.items[0]),"SELECT * FROM [dbo].table1");});
test("Editing an existing alias preserves its column suffix",()=>{const r=suggest("SELECT t|.Name FROM dbo.table1 t");assert.equal(r.applied(r.items[0]),"SELECT [t].Name FROM dbo.table1 t");});
test("Editing a schema-qualified table reference preserves column suffix",()=>{const r=suggest("SELECT dbo.ta|ble1.Name FROM dbo.table1");assert.equal(r.applied(r.items[0]),"SELECT dbo.[table1].Name FROM dbo.table1");});
console.log(`SUCCESS: ${checks} SQL completion checks passed.`);
