# AdminerDOTNET

A compact LocalDB browser and editor built with .NET 10 Razor Pages and Microsoft.Data.SqlClient. This version is intended for Visual Studio Debug sessions on Windows, including an x64 process on Windows ARM64.

## Run in Visual Studio

1. Open `AdminerDOTNET.sln` in a Visual Studio installation that supports .NET 10 and has the ASP.NET/web development workload.
2. Select **Debug | x64**, make **AdminerDOTNET** the startup project, and choose the **AdminerDOTNET** launch profile (Project/Kestrel).
3. Press **F5**. The browser opens at **http://127.0.0.1:5186**. Stop debugging in Visual Studio to stop the app.

No publishing or deployment is required. The .NET 10 SDK, x64 .NET 10 ASP.NET Core runtime, and an existing LocalDB installation are needed. Restore NuGet packages when prompted. Port 5186 must be free.

## Connection

`appsettings.json` supplies:

```json
{
  "ConnectionStrings": {
    "DefaultConnection": "Server=(localdb)\\MSSQLLocalDB;Database=AdminerTest;Integrated Security=True;TrustServerCertificate=True;"
  }
}
```

To override it without editing the project, right-click the project → **Manage User Secrets**, and put the same JSON structure there with your preferred connection string. The launch profile sets the environment to Development, where User Secrets override appsettings. Standard ASP.NET Core environment-variable and command-line configuration overrides also apply. Restart the app after changing connection configuration so open edit forms cannot refer to a different connection.

The configured database is the default for visits without a `db` query parameter. The database dropdown lists accessible, online user databases on the same server; system databases are excluded. Selecting one changes only `InitialCatalog` on a new connection string builder. The configured server, authentication, and all other connection settings are retained, and configuration files are never rewritten.

The app does not discover/start/stop instances or replace pipe names. Run under the Windows account that owns LocalDB. A default database must be specified; the app never falls back to `master`. Database discovery reads SQL Server's catalogue through the configured default connection, so that database must remain accessible even when browsing another database. Invalid or unavailable selections show an error and do not silently fall back. The x64 project target is intentional for LocalDB name resolution on this ARM64 machine.

## Use

- **Database selection:** choose a database in the sidebar dropdown (it opens automatically with JavaScript; **Open** also works). Each tab carries its database in page URLs and forms, with no shared selection cookie or remembered last database. Switching returns to Overview and clears the selected table, filters, record form, SQL draft, and results. A confirmation protects nonempty SQL and unsaved record changes. The selector is disabled while a SQL command runs in that tab, and becomes available again after completion or cancellation.
- **Overview / sidebar:** browse tables and views within the selected database; use the sidebar search to find an object. The active database appears in page titles, the SQL editor, and record-form headings.
- **Browse / filters:** add conditions and nested **All (AND)** / **Any (OR)** groups, then click **Apply filters**. Column choices and operators follow SQL metadata. SQL Server applies the complete filter and column-header sort before fetching a 100-row page; records are never filtered or sorted in the browser. Text contains/starts/ends are literal searches, not wildcard patterns. NULL and empty text are separate operators. **Clear filters** removes the filter tree while keeping sorting. Applied state is preserved in URLs; old single-filter URLs still work. Limits are 50 nodes, 8 levels of nesting, and 6,000 characters of filter JSON.
- **Paging:** the footer shows the displayed range, exact matching count, exact total table/view count, current page, and total pages. Use First/Previous/numbered pages/Next/Last or type a page number and press **Go**. Applying filters or changing sort starts at page 1. Out-of-range pages clamp to the valid range; empty results display no page links. Counts use SQL `COUNT_BIG`; concurrent changes can alter totals between requests. Tables without primary keys do not guarantee stable ordering between requests.
- **Select / Delete selected:** check individual rows or use the header checkbox to select the current page (up to 100 rows). Partial selection shows a mixed header checkbox and the toolbar reports the selected count. **Delete selected** is enabled only for a nonempty selection and confirms the count before submitting. All selected deletions share one transaction: a constraint failure, stale/deleted row, invalid token, or unsupported match rolls back the entire batch. For keyless duplicates, each selected copy deletes one matching row. Navigation, sorting, filtering, paging, and deletion clear selection. Success/error returns to the same filters/sort/page; pagination clamps if the last page disappears. Selection is unavailable in custom SQL results, views, or tables that cannot be matched reliably. JavaScript enables selection controls and prevents repeated submission; antiforgery and row-token checks are enforced server-side.
- **SQL for this view:** the editable textarea above the filters is generated on page load, using the same command definition and typed parameters as Browse. Its `DECLARE` statements make the script independently executable, including the current page offset. Changing filter controls does not update this SQL until you apply them. **Execute** runs the edited batch directly on Browse and replaces the table grid with custom results; joins and arbitrary result shapes are supported. Filters, record actions, table sorting, and table pagination are suspended in this mode. **Reset SQL** returns to the last applied generated table view and restores its script; it does not rerun the edited SQL. The SQL command page remains available separately.
- **Structure:** columns, defaults, primary keys, foreign keys, and index columns, including included columns and sort direction.
- **Insert / Edit / Delete:** ordinary tables allow inserts even without a primary key. Choose **Value**, **NULL**, or **Default / omitted** for each writable field. Empty Value is empty text, not NULL. On insert, Default omits the column; on update it assigns SQL `DEFAULT`. SQL Server validates required columns, relationships, and constraints. Use ISO date/time values, `1`/`0` for bits, and `0x` hexadecimal for binary. Existing primary keys, identity, computed, rowversion, generated, and unsupported editing types are read-only. Views remain read-only through forms. Use SQL for unsupported form types or to change primary keys.
- **Concurrency:** signed row snapshots detect modified/deleted records and changed schemas, and are bound to the configured connection and selected database. Tokens from another server/database are rejected even if table IDs and structures match. **Reload current record** discards form changes and loads the latest values. Successful record forms affect exactly one row in a transaction. Existing edit forms from before the database-selection update must be reopened.
- **Keyless rows:** update/delete match all original column values with NULL-aware, typed comparisons and affect one row using a locked `TOP (1)` CTE. Identical copies are indistinguishable: only one is changed, and another identical copy can still match an old token. A missing/changed match is rejected. Legacy text/image/XML values participate in matching; text comparisons preserve case and trailing spaces. Types that cannot be matched reliably disable update/delete forms rather than generating a partial match. SQL Server remains responsible for constraints and relationships.
- **SQL command:** Run SQL or **Ctrl+Enter** executes one T-SQL batch. No `GO` separators. Multiple result sets, messages, affected counts, and elapsed time are displayed. Display is limited to 1,000 rows per set, 10,000 cells overall, 100 result sets, and 100 informational messages. Truncation does not stop execution. Large individual cell values are not capped. Table browsing previews long cells; the full value is available in the edit form or SQL results.
- **Cancel / timeout:** execution supports cancellation and a 60-second command timeout. Cancellation or errors do not undo statements that already committed. Every submission uses a new connection; do not rely on session state or transactions across submissions. Uncommitted transactions are rolled back when the connection is closed.

The server binds only to IPv4 loopback. Requests must have a permitted local Host; POSTs use antiforgery tokens and reject cross-origin requests. SQL cancellation must match both the execution ID and its database. Arbitrary user-authored SQL retains the configured identity's permissions, including the ability to reference other databases; the dropdown is not a SQL security boundary. This is a local development tool, not a multi-user service. Use no public hosting.

The response uses `Referrer-Policy: same-origin`. The previous `no-referrer` policy caused native browser form POSTs to send `Origin: null`, triggering the origin check and the access-denied page. The updated policy retains the local origin while withholding referrers from other origins; origin and antiforgery protections remain enabled. Reload old tabs after updating. An optional browser diagnostic, `AdminerDOTNET.Tests.exe --origin-probe`, compares the two policies at `http://127.0.0.1:5187`; it performs no database access and stops with Ctrl+C.

## Validation

The dependency-free verification executable exercises the actual services plus HTTP form/antiforgery behavior. Run the Debug app first, then, from the repository directory:

```powershell
dotnet build Tests\AdminerDOTNET.Tests.csproj --configuration Debug
.\Tests\bin\Debug\net10.0\win-x64\AdminerDOTNET.Tests.exe
```

The suite physically connects to **only AdminerTest** on `(localdb)\MSSQLLocalDB`, checks `DB_NAME()` before mutations, and creates uniquely named disposable tables/views there. It removes only those objects in `finally`; it never drops the database or stops LocalDB. The live catalogue check reads database names/access metadata through AdminerTest without opening other databases. Multiple-database checks use a simulated catalogue/connection factory: other names are mapped to a guarded AdminerTest connection, never to real additional databases. A full run includes an actual 60-second timeout test. Use `--skip-timeout` to skip that slow check on subsequent runs.

Tests cover composite keys, unusual identifiers, precision-38 numerics, nullable text, defaults, identity/computed/rowversion fields, parameterization, concurrency conflicts, pagination, filters, multiple SQL results, truncation, errors, cancellation, schema changes, and protected HTTP forms. Selection checks additionally cover independent tabs sharing cookies, default visits, invalid/system database rejection, unusual database-name escaping, links/forms/redirects, token replay across servers/databases, selection-aware cancellation, and discovery/connection failures.

Browse checks cover nested filter precedence and validation, exact counts, boundary pages, SQL-script equivalence for typed parameters, NULL/empty/Unicode/injection cases, keyless duplicate handling, legacy types, constraints, HTTP record operations, custom joined result shapes, Reset SQL, and same-origin form requests. Every live Browse check explicitly selects AdminerTest.

Bulk-delete checks cover transactional rollback after constraints or concurrent changes, keyless duplicate multiplicity, invalid/repeated/cross-scope tokens, schema changes, the 100-row selection limit, protected HTTP submission, current-page controls, retained navigation state, and page clamping after deleting the last page.

The solution build is available with `dotnet build AdminerDOTNET.sln --configuration Debug`. Command-line execution of the app uses `dotnet run --project AdminerDOTNET.csproj`; Visual Studio F5 remains the intended launch workflow.

## Current boundaries

No server selector, shared/remembered database selection, visual schema designer, import/export, query history, application database, or migrations. Schema changes are available through SQL. Publishing and release packaging are intentionally deferred.
