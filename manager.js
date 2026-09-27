const form = document.querySelector("#manager-form");
const status = document.querySelector("#status");
const results = document.querySelector("#results");
const columns = [
  ["Name", "guestName"],
  ["Email", "guestEmail"],
  ["Selected match", "gameChoice"],
  ["Match / stay preferences", "guestNote"],
  ["Received (UTC)", "createdAtUtc"],
];

form.addEventListener("submit", async (event) => {
  event.preventDefault();
  status.className = "status";
  status.textContent = "Loading enquiries…";
  results.replaceChildren();
  const keyInput = document.querySelector("#admin-key");

  try {
    const response = await fetch(`${document.querySelector("#api-base").value.replace(/\/$/, "")}/api/enquiries`, {
      headers: { "X-Admin-Key": keyInput.value },
      cache: "no-store",
      referrerPolicy: "no-referrer",
    });
    if (response.status === 401) throw new Error("Unauthorised. Check the manager key.");
    if (response.status === 403) throw new Error("Too many wrong keys from this device. Access is paused for 15 minutes.");
    if (!response.ok) throw new Error("The enquiry list could not be loaded.");
    const enquiries = await response.json();
    status.textContent = `${enquiries.length} entr${enquiries.length === 1 ? "y" : "ies"} saved.`;
    results.append(enquiries.length ? buildTable(enquiries) : emptyMessage());
  } catch (error) {
    status.className = "status error";
    status.textContent = error.message.includes("Failed to fetch") ? "The API is not running or is unreachable." : error.message;
  }
});

// Built with textContent only, so guest-supplied text can never become markup.
function buildTable(enquiries) {
  const table = document.createElement("table");
  const headerRow = table.createTHead().insertRow();
  columns.forEach(([label]) => {
    const th = document.createElement("th");
    th.textContent = label;
    headerRow.append(th);
  });

  const body = table.createTBody();
  enquiries.forEach((item) => {
    const row = body.insertRow();
    columns.forEach(([, field]) => {
      row.insertCell().textContent = item[field] ?? "";
    });
  });
  return table;
}

function emptyMessage() {
  const message = document.createElement("p");
  message.className = "empty";
  message.textContent = "No enquiries have been submitted yet.";
  return message;
}
