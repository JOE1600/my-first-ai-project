const form = document.querySelector("#manager-form");
const apiInput = document.querySelector("#api-base");
const keyInput = document.querySelector("#admin-key");
const loadButton = document.querySelector("#load-button");
const lockButton = document.querySelector("#lock-button");
const status = document.querySelector("#status");
const dashboard = document.querySelector("#dashboard");
const results = document.querySelector("#results");
const resultCount = document.querySelector("#result-count");
const searchInput = document.querySelector("#search");
const matchFilter = document.querySelector("#match-filter");

const API_STORAGE_KEY = "boxwood-manager-api";
const IDLE_LOCK_MS = 30 * 60 * 1000;
const DAY_MS = 24 * 60 * 60 * 1000;
const hobartTime = new Intl.DateTimeFormat("en-AU", {
  timeZone: "Australia/Hobart",
  weekday: "short",
  day: "numeric",
  month: "short",
  year: "numeric",
  hour: "numeric",
  minute: "2-digit",
});
const relativeTime = new Intl.RelativeTimeFormat("en-AU", { numeric: "auto" });

let enquiries = [];
let idleTimer = 0;

// Only the API address is remembered. The manager key stays in memory for this tab.
try {
  const savedApi = localStorage.getItem(API_STORAGE_KEY);
  if (savedApi) apiInput.value = savedApi;
} catch {
  // Storage can be blocked (private windows); the default address still works.
}

form.addEventListener("submit", (event) => {
  event.preventDefault();
  loadEnquiries();
});
document.querySelector("#refresh-button").addEventListener("click", () => loadEnquiries());
document.querySelector("#export-button").addEventListener("click", exportCsv);
lockButton.addEventListener("click", () => lock("Locked. Enter the manager key to view enquiries again."));
searchInput.addEventListener("input", render);
matchFilter.addEventListener("change", render);
["pointerdown", "keydown"].forEach((type) => document.addEventListener(type, resetIdleTimer, { passive: true }));

async function loadEnquiries() {
  const apiBase = apiInput.value.trim().replace(/\/+$/, "");
  if (!keyInput.value) {
    setStatus("Enter the manager key first.", true);
    keyInput.focus();
    return;
  }

  setStatus("Loading enquiries…");
  loadButton.disabled = true;
  try {
    const response = await fetch(`${apiBase}/api/enquiries`, {
      headers: { "X-Admin-Key": keyInput.value },
      cache: "no-store",
      referrerPolicy: "no-referrer",
    });
    if (response.status === 401) throw new Error("Unauthorised. Check the manager key.");
    if (response.status === 403) throw new Error("Too many wrong keys from this device. Access is paused for 15 minutes.");
    if (response.status === 429) throw new Error("Too many requests. Wait a minute and try again.");
    if (!response.ok) throw new Error("The enquiry list could not be loaded.");

    enquiries = await response.json();
    try {
      localStorage.setItem(API_STORAGE_KEY, apiBase);
    } catch {
      // Not remembering the address is fine.
    }

    const loadedAt = new Date().toLocaleTimeString("en-AU", { hour: "numeric", minute: "2-digit" });
    setStatus(`Connected. ${plural(enquiries.length, "enquiry", "enquiries")} loaded at ${loadedAt}.`);
    lockButton.hidden = false;
    dashboard.hidden = false;
    fillMatchFilter();
    renderStats();
    render();
    resetIdleTimer();
  } catch (error) {
    const unreachable = error instanceof TypeError;
    setStatus(unreachable ? "The API is not running or is unreachable at that address." : error.message, true);
  } finally {
    loadButton.disabled = false;
  }
}

function lock(message) {
  keyInput.value = "";
  enquiries = [];
  results.replaceChildren();
  dashboard.hidden = true;
  lockButton.hidden = true;
  searchInput.value = "";
  window.clearTimeout(idleTimer);
  setStatus(message);
}

function resetIdleTimer() {
  if (dashboard.hidden) return;
  window.clearTimeout(idleTimer);
  idleTimer = window.setTimeout(() => lock("Locked after 30 minutes without activity."), IDLE_LOCK_MS);
}

function setStatus(message, isError = false) {
  status.textContent = message;
  status.classList.toggle("error", isError);
}

function matchLabel(choice) {
  if (choice === "future") return "Another home game";
  if (choice === "next") return "Next home game";
  return choice;
}

function fillMatchFilter() {
  const selected = matchFilter.value;
  const choices = [...new Set(enquiries.map((item) => item.gameChoice))].sort();
  matchFilter.replaceChildren(new Option("All matches", ""));
  choices.forEach((choice) => matchFilter.append(new Option(matchLabel(choice), choice)));
  matchFilter.value = choices.includes(selected) ? selected : "";
}

function renderStats() {
  const weekAgo = Date.now() - 7 * DAY_MS;
  document.querySelector("#stat-total").textContent = enquiries.length;
  document.querySelector("#stat-week").textContent = enquiries.filter((item) => Date.parse(item.createdAtUtc) >= weekAgo).length;

  const counts = new Map();
  enquiries.forEach((item) => counts.set(item.gameChoice, (counts.get(item.gameChoice) || 0) + 1));
  const [topChoice, topCount] = [...counts].sort((a, b) => b[1] - a[1])[0] || [];
  document.querySelector("#stat-match").textContent = topChoice ? `${matchLabel(topChoice)} (${topCount})` : "–";
}

function filtered() {
  const query = searchInput.value.trim().toLowerCase();
  return enquiries.filter((item) => {
    if (matchFilter.value && item.gameChoice !== matchFilter.value) return false;
    if (!query) return true;
    return [item.guestName, item.guestEmail, item.guestNote, matchLabel(item.gameChoice)]
      .some((value) => (value || "").toLowerCase().includes(query));
  });
}

function render() {
  const visible = filtered();
  resultCount.textContent = visible.length === enquiries.length
    ? `Showing all ${plural(enquiries.length, "enquiry", "enquiries")}, newest first.`
    : `Showing ${visible.length} of ${plural(enquiries.length, "enquiry", "enquiries")}.`;

  if (!visible.length) {
    const empty = document.createElement("p");
    empty.className = "empty-state";
    empty.textContent = enquiries.length ? "No enquiries match your search." : "No enquiries have been submitted yet.";
    results.replaceChildren(empty);
    return;
  }

  results.replaceChildren(buildTable(visible));
}

// Built with textContent and DOM properties only, so guest-supplied text can never become markup.
function buildTable(items) {
  const table = document.createElement("table");
  table.className = "enquiries";
  const headerRow = table.createTHead().insertRow();
  ["Guest", "Match", "Match / stay preferences", "Received (Hobart)", ""].forEach((label) => {
    const th = document.createElement("th");
    th.scope = "col";
    th.textContent = label;
    headerRow.append(th);
  });

  const body = table.createTBody();
  items.forEach((item) => {
    const row = body.insertRow();

    const guest = cell(row, "Guest");
    const name = element("span", "guest-name", item.guestName);
    if (Date.now() - Date.parse(item.createdAtUtc) < DAY_MS) name.append(element("span", "badge", "New"));
    guest.append(name, element("span", "guest-email", item.guestEmail));

    cell(row, "Match").append(element("span", "match", matchLabel(item.gameChoice)));

    const note = item.guestNote?.trim();
    cell(row, "Preferences").append(element("span", note ? "guest-note" : "guest-note empty", note || "No preferences given"));

    const received = cell(row, "Received");
    received.className = "received";
    const time = element("time", "", hobartTime.format(new Date(item.createdAtUtc)));
    time.dateTime = item.createdAtUtc;
    received.append(time, element("small", "", timeAgo(item.createdAtUtc)));

    const actions = cell(row, "");
    actions.removeAttribute("data-label");
    const wrap = element("div", "row-actions", "");
    const reply = element("a", "reply", "Reply");
    reply.href = replyLink(item);
    const copy = element("button", "", "Copy email");
    copy.type = "button";
    copy.addEventListener("click", () => copyEmail(item.guestEmail, copy));
    wrap.append(reply, copy);
    actions.append(wrap);
  });
  return table;
}

function cell(row, label) {
  const td = row.insertCell();
  if (label) td.dataset.label = label;
  return td;
}

function element(tag, className, text) {
  const node = document.createElement(tag);
  if (className) node.className = className;
  if (text) node.textContent = text;
  return node;
}

function replyLink(item) {
  const firstName = item.guestName.split(/\s+/)[0];
  const subject = "Your Stay & Play game-night enquiry";
  const body = `Hi ${firstName},\n\nThank you for your enquiry about ${matchLabel(item.gameChoice)}.\n\n\n\nKind regards,\nBoxwood x Islington Hotel`;
  return `mailto:${encodeURIComponent(item.guestEmail)}?subject=${encodeURIComponent(subject)}&body=${encodeURIComponent(body)}`;
}

async function copyEmail(email, button) {
  try {
    await navigator.clipboard.writeText(email);
    button.textContent = "Copied";
  } catch {
    button.textContent = "Copy failed";
  }
  window.setTimeout(() => { button.textContent = "Copy email"; }, 1600);
}

function timeAgo(iso) {
  const minutes = Math.round((Date.parse(iso) - Date.now()) / 60000);
  if (Math.abs(minutes) < 60) return relativeTime.format(minutes, "minute");
  const hours = Math.round(minutes / 60);
  if (Math.abs(hours) < 24) return relativeTime.format(hours, "hour");
  return relativeTime.format(Math.round(hours / 24), "day");
}

function plural(count, one, many) {
  return `${count} ${count === 1 ? one : many}`;
}

// Exports what is currently shown. Cells that start with =, +, - or @ are prefixed so
// spreadsheet apps treat guest text as text, not as a formula.
function exportCsv() {
  const rows = [["Id", "Name", "Email", "Match", "Preferences", "Received (UTC)"]];
  filtered().forEach((item) => rows.push([
    item.id, item.guestName, item.guestEmail, matchLabel(item.gameChoice), item.guestNote || "", item.createdAtUtc,
  ]));
  const csv = rows.map((row) => row.map(csvCell).join(",")).join("\r\n");
  const url = URL.createObjectURL(new Blob([`﻿${csv}`], { type: "text/csv;charset=utf-8" }));
  const link = document.createElement("a");
  link.href = url;
  link.download = `boxwood-enquiries-${new Date().toISOString().slice(0, 10)}.csv`;
  link.click();
  window.setTimeout(() => URL.revokeObjectURL(url), 1000);
}

function csvCell(value) {
  let text = String(value ?? "");
  if (/^[=+\-@\t\r]/.test(text)) text = `'${text}`;
  return `"${text.replace(/"/g, '""')}"`;
}
