const uploadForm = document.querySelector("#upload-form");
const fileInput = document.querySelector("#database-file");
const workspace = document.querySelector("#workspace");
const statusElement = document.querySelector("#status");
const metadataElement = document.querySelector("#metadata");
const tablesElement = document.querySelector("#tables");
const queryForm = document.querySelector("#query-form");
const sqlInput = document.querySelector("#sql");
const queryOutput = document.querySelector("#query-output");
const pageForm = document.querySelector("#page-form");
const pageOutput = document.querySelector("#page-output");
const treeOutput = document.querySelector("#tree-output");
const treeLabel = document.querySelector("#tree-label");

let overview;

async function request(path, options) {
  const response = await fetch(path, options);
  const payload = response.headers
    .get("content-type")
    ?.includes("application/json")
    ? await response.json()
    : null;

  if (!response.ok) {
    throw new Error(
      payload?.error || payload?.title || "The request could not be completed.",
    );
  }

  return payload;
}

function escapeHtml(value) {
  return String(value).replace(
    /[&<>"]/g,
    (character) =>
      ({
        "&": "&amp;",
        "<": "&lt;",
        ">": "&gt;",
        '"': "&quot;",
      })[character],
  );
}

function formatBytes(value) {
  return value < 1024 * 1024
    ? `${(value / 1024).toFixed(1)} KB`
    : `${(value / (1024 * 1024)).toFixed(2)} MB`;
}

function setStatus(message, isError = false) {
  statusElement.textContent = message;
  statusElement.classList.toggle("error", isError);
}

function setOutput(element, html) {
  element.classList.remove("empty");
  element.innerHTML = html;
}

function renderOverview(response) {
  overview = response;
  const database = response.database;
  document.querySelector("#file-name").textContent = response.fileName;
  metadataElement.innerHTML = [
    ["Page size", `${database.pageSize.toLocaleString()} bytes`],
    ["Pages", database.pageCount.toLocaleString()],
    ["Encoding", database.textEncoding],
    ["File size", formatBytes(database.fileSizeBytes)],
    ["Tables", database.tables.length],
  ]
    .map(
      ([label, value]) =>
        `<div><dt>${label}</dt><dd>${escapeHtml(value)}</dd></div>`,
    )
    .join("");

  tablesElement.innerHTML = database.tables.length
    ? database.tables
        .map(
          (table) => `
      <article class="table-card">
        <h3>${escapeHtml(table.name)}</h3>
        <p>Root page ${table.rootPage} · ${table.columns.length} columns</p>
        <div class="button-row">
          <button type="button" data-query-table="${escapeHtml(table.name)}">Query</button>
          <button type="button" data-tree-table="${escapeHtml(table.name)}">B-tree</button>
        </div>
      </article>`,
        )
        .join("")
    : '<p class="muted">No user tables were found.</p>';

  document.querySelectorAll("[data-query-table]").forEach((button) => {
    button.addEventListener("click", () => {
      sqlInput.value = `SELECT * FROM ${button.dataset.queryTable}`;
      runQuery();
    });
  });
  document
    .querySelectorAll("[data-tree-table]")
    .forEach((button) =>
      button.addEventListener("click", () =>
        loadTree(button.dataset.treeTable),
      ),
    );
}

function renderQuery(result) {
  const header = result.columns
    .map((column) => `<th>${escapeHtml(column)}</th>`)
    .join("");
  const rows = result.rows
    .map(
      (row) =>
        `<tr>${row.map((value) => `<td>${escapeHtml(value)}</td>`).join("")}</tr>`,
    )
    .join("");
  const notice = result.truncated
    ? '<p class="notice">Showing the first 500 matching rows.</p>'
    : "";
  setOutput(
    queryOutput,
    `${notice}<div class="table-scroll"><table><thead><tr>${header}</tr></thead><tbody>${rows || `<tr><td colspan="${result.columns.length}">No rows matched this query.</td></tr>`}</tbody></table></div>`,
  );
}

function renderPage(page) {
  const cells = page.cells.length
    ? `<ul class="cell-list">${page.cells.map((cell) => `<li><strong>Cell ${cell.index + 1}</strong><span>offset ${cell.offset}${cell.rowId === null ? "" : ` · row ID ${cell.rowId}`}${cell.payloadSize === null ? "" : ` · ${cell.payloadSize} payload bytes`}</span>${cell.overflowPages.length ? `<em>Overflow pages: ${cell.overflowPages.join(", ")}</em>` : ""}</li>`).join("")}</ul>`
    : "<p>No table-leaf cells to display for this page type.</p>";
  const children = page.childPages.length
    ? `<p class="children">Child pages: ${page.childPages.join(", ")}</p>`
    : "";
  setOutput(
    pageOutput,
    `<p class="page-type">${escapeHtml(page.pageType)}</p><p>${page.cellCount} cells</p>${children}${cells}<h3>First bytes</h3><pre>${page.hexPreview.match(/.{1,32}/g).join("\n")}</pre>`,
  );
}

function renderTree(tableName, nodes) {
  treeLabel.textContent = tableName;
  setOutput(
    treeOutput,
    `<div class="tree">${nodes.map((node) => `<article><strong>Page ${node.pageNumber}</strong><span>${escapeHtml(node.pageType)}</span><small>${node.cellCount} cells${node.childPages.length ? ` → ${node.childPages.join(", ")}` : ""}</small><button type="button" data-page="${node.pageNumber}">Inspect page</button></article>`).join("")}</div>`,
  );
  treeOutput
    .querySelectorAll("[data-page]")
    .forEach((button) =>
      button.addEventListener("click", () =>
        inspectPage(Number(button.dataset.page)),
      ),
    );
}

async function runQuery() {
  try {
    setStatus("Running query through the custom reader…");
    renderQuery(
      await request("/api/query", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ sql: sqlInput.value }),
      }),
    );
    setStatus("Query completed. The uploaded database was not modified.");
  } catch (error) {
    setStatus(error.message, true);
  }
}

async function inspectPage(pageNumber) {
  try {
    document.querySelector("#page-number").value = pageNumber;
    setOutput(pageOutput, "Loading page…");
    renderPage(await request(`/api/pages/${pageNumber}`));
  } catch (error) {
    setStatus(error.message, true);
  }
}

async function loadTree(tableName) {
  try {
    treeOutput.textContent = "Traversing the B-tree…";
    renderTree(
      tableName,
      await request(`/api/tables/${encodeURIComponent(tableName)}/tree`),
    );
  } catch (error) {
    setStatus(error.message, true);
  }
}

uploadForm.addEventListener("submit", async (event) => {
  event.preventDefault();
  if (!fileInput.files[0]) return;

  try {
    setStatus("Validating and parsing the selected database…");
    const formData = new FormData();
    formData.append("file", fileInput.files[0]);
    renderOverview(
      await request("/api/database", { method: "POST", body: formData }),
    );
    workspace.hidden = false;
    setStatus("Database opened with the custom read-only parser.");
    const firstTable = overview.database.tables[0];
    if (firstTable) {
      sqlInput.value = `SELECT * FROM ${firstTable.name}`;
      await Promise.all([
        runQuery(),
        inspectPage(firstTable.rootPage),
        loadTree(firstTable.name),
      ]);
    }
  } catch (error) {
    workspace.hidden = true;
    setStatus(error.message, true);
  }
});

queryForm.addEventListener("submit", (event) => {
  event.preventDefault();
  runQuery();
});
pageForm.addEventListener("submit", (event) => {
  event.preventDefault();
  inspectPage(Number(document.querySelector("#page-number").value));
});
