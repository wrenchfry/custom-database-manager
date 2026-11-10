const uploadForm = document.querySelector("#upload-form");
const fileInput = document.querySelector("#database-file");
const uploadButton = uploadForm.querySelector('button[type="submit"]');
const workspace = document.querySelector("#workspace");
const statusElement = document.querySelector("#status");
const metadataElement = document.querySelector("#metadata");
const tablesElement = document.querySelector("#tables");
const tableCountElement = document.querySelector("#table-count");
const selectedTableElement = document.querySelector("#selected-table");
const queryForm = document.querySelector("#query-form");
const sqlInput = document.querySelector("#sql");
const queryButton = queryForm.querySelector('button[type="submit"]');
const queryOutput = document.querySelector("#query-output");
const treeOutput = document.querySelector("#tree-output");
const queryView = document.querySelector("#query-view");
const treeView = document.querySelector("#tree-view");
const pageDialog = document.querySelector("#page-dialog");
const pageDialogTitle = document.querySelector("#page-dialog-title");
const pageOutput = document.querySelector("#page-output");
const viewButtons = document.querySelectorAll("[data-view]");

let tables = [];
let selectedTable = null;
let activeView = "query";

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
  return String(value ?? "").replace(
    /[&<>"']/g,
    (character) =>
      ({
        "&": "&amp;",
        "<": "&lt;",
        ">": "&gt;",
        '"': "&quot;",
        "'": "&#39;",
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

function setEmptyOutput(element, message) {
  element.textContent = message;
  element.classList.add("empty");
}

function renderOverview(response) {
  const database = response.database;
  tables = database.tables;
  selectedTable = null;

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

  tableCountElement.textContent = String(tables.length);
  tablesElement.innerHTML = tables.length
    ? tables
        .map(
          (table, index) => `
            <button
              type="button"
              class="table-choice"
              data-table-index="${index}"
              aria-pressed="false"
            >
              <span>${escapeHtml(table.name)}</span>
              <small>Root page ${table.rootPage} · ${table.columns.length} columns</small>
            </button>`,
        )
        .join("")
    : '<p class="muted">No supported tables were found in this file.</p>';

  workspace.hidden = false;
  sqlInput.disabled = tables.length === 0;
  queryButton.disabled = tables.length === 0;
  selectedTableElement.textContent = tables.length
    ? "Choose a table"
    : "No supported tables";
  setEmptyOutput(
    queryOutput,
    tables.length
      ? "Choose a table to start."
      : "This database does not contain a supported table.",
  );
  setEmptyOutput(
    treeOutput,
    tables.length
      ? "Choose a table, then open Storage pages."
      : "This database does not contain a supported table.",
  );

  if (tables.length > 0) {
    selectTable(0, true);
  }
}

function selectTable(index, runQuery = false) {
  const table = tables[index];
  if (!table) {
    return;
  }

  selectedTable = table;
  selectedTableElement.textContent = table.name;
  sqlInput.value = `SELECT * FROM ${table.name}`;
  sqlInput.disabled = false;
  queryButton.disabled = false;

  tablesElement.querySelectorAll("[data-table-index]").forEach((button) => {
    const isSelected = Number(button.dataset.tableIndex) === index;
    button.setAttribute("aria-pressed", String(isSelected));
  });

  setEmptyOutput(queryOutput, `Run a query to read rows from ${table.name}.`);
  setEmptyOutput(
    treeOutput,
    `Select Storage pages to inspect the B-tree for ${table.name}.`,
  );
  if (activeView === "tree") {
    setView("query");
  }
  if (runQuery) {
    runQueryForSelectedTable();
  }
}

function setView(view) {
  activeView = view;
  const showTree = view === "tree";
  queryView.hidden = showTree;
  treeView.hidden = !showTree;
  viewButtons.forEach((button) => {
    const isActive = button.dataset.view === view;
    button.classList.toggle("active", isActive);
    button.setAttribute("aria-pressed", String(isActive));
  });

  if (showTree && selectedTable) {
    loadTree(selectedTable.name);
  }
}

function renderQuery(result) {
  const headers = result.columns
    .map((column) => `<th scope="col">${escapeHtml(column)}</th>`)
    .join("");
  const rows = result.rows
    .map(
      (row) =>
        `<tr>${row.map((value) => `<td>${escapeHtml(value)}</td>`).join("")}</tr>`,
    )
    .join("");
  const count = result.rows.length;
  const message = result.truncated
    ? `Showing the first ${count} rows. Narrow the query to see fewer results.`
    : `${count} ${count === 1 ? "row" : "rows"} returned.`;
  const notice = result.truncated
    ? '<p class="notice">Results are limited to 500 rows.</p>'
    : "";
  const emptyRow = result.columns.length
    ? `<tr><td colspan="${result.columns.length}">No rows matched this query.</td></tr>`
    : "";

  queryOutput.classList.remove("empty");
  queryOutput.innerHTML = `
    <p class="result-summary">${message}</p>
    ${notice}
    <div class="table-scroll">
      <table>
        <thead><tr>${headers}</tr></thead>
        <tbody>${rows || emptyRow}</tbody>
      </table>
    </div>`;
}

function renderTree(nodes) {
  const pages = nodes
    .map((node) => {
      const children = node.childPages.length
        ? `Child pages: ${node.childPages.join(", ")}`
        : "No child pages";
      return `
        <article class="page-card">
          <div class="page-card-main">
            <strong>Page ${node.pageNumber}</strong>
            <span class="page-kind">${escapeHtml(node.pageType)}</span>
          </div>
          <p>${node.cellCount} ${node.cellCount === 1 ? "cell" : "cells"} · ${children}</p>
          <button type="button" data-page-number="${node.pageNumber}">
            Inspect page
          </button>
        </article>`;
    })
    .join("");

  treeOutput.classList.remove("empty");
  treeOutput.innerHTML = `
    <p class="tree-explainer">
      Start at the table’s root page. Interior pages point to child pages;
      leaf pages hold table records.
    </p>
    <div class="tree-list">${pages}</div>`;
}

function renderPage(page) {
  const cells = page.cells.length
    ? `<ul class="cell-list">${page.cells
        .map(
          (cell) => `
            <li>
              <strong>Cell ${cell.index + 1}</strong>
              <span>
                Offset ${cell.offset}
                ${cell.rowId === null ? "" : ` · row ID ${cell.rowId}`}
                ${cell.payloadSize === null ? "" : ` · ${cell.payloadSize} payload bytes`}
              </span>
              ${
                cell.overflowPages.length
                  ? `<em>Overflow pages: ${cell.overflowPages.join(", ")}</em>`
                  : ""
              }
            </li>`,
        )
        .join("")}</ul>`
    : "<p>This page has no table-leaf cells to display.</p>";
  const children = page.childPages.length
    ? `<p class="children">Child pages: ${page.childPages.join(", ")}</p>`
    : "";
  const hexLines = (page.hexPreview.match(/.{1,32}/g) ?? []).join("\n");

  pageDialogTitle.textContent = `Page ${page.pageNumber}`;
  pageOutput.innerHTML = `
    <p class="page-type">${escapeHtml(page.pageType)}</p>
    <p>${page.cellCount} ${page.cellCount === 1 ? "cell" : "cells"}</p>
    ${children}
    ${cells}
    <h3>First bytes (hexadecimal)</h3>
    <pre>${escapeHtml(hexLines)}</pre>`;
}

async function runQueryForSelectedTable() {
  if (!selectedTable) {
    return;
  }

  setStatus(`Running query on ${selectedTable.name}…`);
  try {
    const result = await request("/api/query", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ sql: sqlInput.value }),
    });
    renderQuery(result);
    setStatus("Query complete. The database file was not modified.");
  } catch (error) {
    setEmptyOutput(queryOutput, error.message);
    setStatus(error.message, true);
  }
}

async function loadTree(tableName) {
  setEmptyOutput(treeOutput, `Loading storage pages for ${tableName}…`);
  try {
    const nodes = await request(
      `/api/tables/${encodeURIComponent(tableName)}/tree`,
    );
    if (selectedTable?.name !== tableName || activeView !== "tree") {
      return;
    }
    if (nodes.length === 0) {
      setEmptyOutput(treeOutput, "No B-tree pages were found for this table.");
      return;
    }
    renderTree(nodes);
    setStatus(`Showing the B-tree pages for ${tableName}.`);
  } catch (error) {
    setEmptyOutput(treeOutput, error.message);
    setStatus(error.message, true);
  }
}

async function inspectPage(pageNumber) {
  pageDialogTitle.textContent = `Page ${pageNumber}`;
  pageOutput.textContent = "Loading page details…";
  if (!pageDialog.open) {
    pageDialog.showModal();
  }

  try {
    renderPage(await request(`/api/pages/${pageNumber}`));
  } catch (error) {
    pageOutput.textContent = error.message;
    setStatus(error.message, true);
  }
}

uploadForm.addEventListener("submit", async (event) => {
  event.preventDefault();
  const file = fileInput.files[0];
  if (!file) {
    setStatus("Choose a SQLite database file first.", true);
    return;
  }

  uploadButton.disabled = true;
  setStatus("Opening and checking the database…");
  try {
    const formData = new FormData();
    formData.append("file", file);
    renderOverview(
      await request("/api/database", { method: "POST", body: formData }),
    );
    setStatus(
      tables.length
        ? `Opened ${file.name}. Choose a table or run the suggested query.`
        : `Opened ${file.name}, but no supported tables were found.`,
    );
  } catch (error) {
    workspace.hidden = true;
    setStatus(error.message, true);
  } finally {
    uploadButton.disabled = false;
  }
});

tablesElement.addEventListener("click", (event) => {
  const button = event.target.closest("[data-table-index]");
  if (button) {
    selectTable(Number(button.dataset.tableIndex));
  }
});

viewButtons.forEach((button) => {
  button.addEventListener("click", () => {
    if (!selectedTable) {
      setStatus("Choose a table first.", true);
      return;
    }
    setView(button.dataset.view);
  });
});

queryForm.addEventListener("submit", (event) => {
  event.preventDefault();
  runQueryForSelectedTable();
});

treeOutput.addEventListener("click", (event) => {
  const button = event.target.closest("[data-page-number]");
  if (button) {
    inspectPage(Number(button.dataset.pageNumber));
  }
});

document
  .querySelector("#close-page-dialog")
  .addEventListener("click", () => pageDialog.close());

pageDialog.addEventListener("click", (event) => {
  if (event.target === pageDialog) {
    pageDialog.close();
  }
});
