const menuToggle = document.querySelector(".menu-toggle");
const nav = document.querySelector("nav");
const bookingForm = document.querySelector("#booking-form");
const guestName = document.querySelector("#guest-name");
const guestEmail = document.querySelector("#guest-email");
const guestNote = document.querySelector("#guest-note");
const guestWebsite = document.querySelector("#guest-website");
const formStatus = document.querySelector("#form-status");
const noteCount = document.querySelector("#note-count");
const gameChoice = document.querySelector("#game-choice");
const gameDetail = document.querySelector("#game-detail");
const hotelViewer = document.querySelector("#hotel-viewer");
const hotelPhotoRing = document.querySelector("#hotel-photo-ring");
const apiBase = (window.BOXWOOD_API_BASE ?? "http://localhost:5050").replace(/\/$/, "");
const enquiriesOpen = apiBase !== "";
// Without an API, enquiries can still be sent as a pre-filled email from the guest's own mail app.
const enquiryEmail = (window.BOXWOOD_ENQUIRY_EMAIL ?? "").trim();
const emailEnquiries = !enquiriesOpen && enquiryEmail !== "";

const clientRateLimit = {
  lastSubmission: 0,
  cooldownMs: 15000,
};

if (window.Lenis && !window.matchMedia("(prefers-reduced-motion: reduce)").matches) {
  const lenis = new window.Lenis({
    duration: 1.1,
    smoothWheel: true,
    anchors: true,
  });

  function raf(time) {
    lenis.raf(time);
    window.requestAnimationFrame(raf);
  }

  window.requestAnimationFrame(raf);
}

menuToggle?.addEventListener("click", () => {
  const open = menuToggle.getAttribute("aria-expanded") === "true";
  menuToggle.setAttribute("aria-expanded", String(!open));
  nav.classList.toggle("is-open", !open);
});

document.querySelectorAll("nav a").forEach((link) => {
  link.addEventListener("click", () => {
    nav.classList.remove("is-open");
    menuToggle?.setAttribute("aria-expanded", "false");
  });
});

if (hotelViewer && hotelPhotoRing) {
  const hotelPhotos = Array.from(hotelPhotoRing.querySelectorAll(".hotel-photo-card"));
  const title = document.querySelector("#hotel-view-title");
  const description = document.querySelector("#hotel-view-description");
  const indexLabel = document.querySelector("#hotel-view-index");
  let activePhoto = 0;
  let pointerStart = null;

  function showHotelPhoto(index) {
    activePhoto = (index + hotelPhotos.length) % hotelPhotos.length;
    hotelPhotoRing.style.transform = `rotateY(${-activePhoto * 72}deg)`;

    const photo = hotelPhotos[activePhoto];
    if (title) title.textContent = photo.dataset.title;
    if (description) description.textContent = photo.dataset.description;
    if (indexLabel) {
      indexLabel.textContent = `${String(activePhoto + 1).padStart(2, "0")} / ${String(hotelPhotos.length).padStart(2, "0")}`;
    }
  }

  hotelPhotos.forEach((photo, index) => {
    photo.style.setProperty("--photo-index", String(index));
  });

  document.querySelector("#hotel-view-previous")?.addEventListener("click", () => {
    showHotelPhoto(activePhoto - 1);
  });

  document.querySelector("#hotel-view-next")?.addEventListener("click", () => {
    showHotelPhoto(activePhoto + 1);
  });

  hotelViewer.addEventListener("keydown", (event) => {
    if (event.key === "ArrowLeft" || event.key === "ArrowRight") {
      event.preventDefault();
      showHotelPhoto(activePhoto + (event.key === "ArrowRight" ? 1 : -1));
    }
  });

  hotelViewer.addEventListener("pointerdown", (event) => {
    if (event.button !== 0) return;
    pointerStart = event.clientX;
    hotelViewer.classList.add("is-dragging");
    hotelViewer.setPointerCapture(event.pointerId);
  });

  let dragFrame = 0;
  let dragX = 0;

  // Pointer events can fire several times per frame; only write the transform once per frame.
  hotelViewer.addEventListener("pointermove", (event) => {
    if (pointerStart === null) return;
    dragX = event.clientX;
    if (dragFrame) return;
    dragFrame = window.requestAnimationFrame(() => {
      dragFrame = 0;
      if (pointerStart === null) return;
      const rotation = -activePhoto * 72 + (dragX - pointerStart) * 0.55;
      hotelPhotoRing.style.transform = `rotateY(${rotation}deg)`;
    });
  });

  function finishHotelDrag(event) {
    if (pointerStart === null) return;
    const distance = event.clientX - pointerStart;
    pointerStart = null;
    window.cancelAnimationFrame(dragFrame);
    dragFrame = 0;
    hotelViewer.classList.remove("is-dragging");
    showHotelPhoto(activePhoto - Math.round(distance / 90));
  }

  hotelViewer.addEventListener("pointerup", finishHotelDrag);
  hotelViewer.addEventListener("pointercancel", finishHotelDrag);
  showHotelPhoto(activePhoto);
}

const revealElements = document.querySelectorAll(".reveal");
if ("IntersectionObserver" in window) {
  const revealObserver = new IntersectionObserver(
    (entries) => {
      entries.forEach((entry) => {
        if (entry.isIntersecting) {
          entry.target.classList.add("is-visible");
          revealObserver.unobserve(entry.target);
        }
      });
    },
    { threshold: 0.12 }
  );

  revealElements.forEach((element, index) => {
    element.style.transitionDelay = `${Math.min(index * 70, 280)}ms`;
    revealObserver.observe(element);
  });
} else {
  revealElements.forEach((element) => element.classList.add("is-visible"));
}

document.querySelectorAll(".spotlight-card").forEach((card) => {
  let bounds = null;
  let frame = 0;
  let pointer = { x: 0, y: 0 };

  // Measure once on entry rather than on every move, and batch style writes per frame.
  card.addEventListener("pointerenter", () => {
    bounds = card.getBoundingClientRect();
  });

  card.addEventListener("pointermove", (event) => {
    pointer = { x: event.clientX, y: event.clientY };
    if (frame) return;
    frame = window.requestAnimationFrame(() => {
      frame = 0;
      bounds ??= card.getBoundingClientRect();
      const x = pointer.x - bounds.left;
      const y = pointer.y - bounds.top;
      const tiltX = ((x / bounds.width) - 0.5) * 3;
      const tiltY = ((y / bounds.height) - 0.5) * -3;
      card.style.setProperty("--spot-x", `${x}px`);
      card.style.setProperty("--spot-y", `${y}px`);
      card.style.setProperty("--tilt-x", `${tiltX}deg`);
      card.style.setProperty("--tilt-y", `${tiltY}deg`);
    });
  });

  card.addEventListener("pointerleave", () => {
    window.cancelAnimationFrame(frame);
    frame = 0;
    bounds = null;
    card.style.removeProperty("--tilt-x");
    card.style.removeProperty("--tilt-y");
  });
});

function showGameDetail() {
  if (!gameChoice || !gameDetail) return;

  const selectedOption = gameChoice.selectedOptions[0];
  if (!selectedOption || selectedOption.value === "future") {
    setGameDetail(
      "Ask us about another home game",
      "Tell us your preferred date in the enquiry and we will check availability.",
      "https://www.jackjumpers.com.au/schedule"
    );
    return;
  }

  setGameDetail(
    `JackJumpers vs ${selectedOption.dataset.opponent}`,
    `${selectedOption.dataset.date} · ${selectedOption.dataset.tipoff} · Home game`,
    selectedOption.dataset.url
  );
}

function setGameDetail(title, description, linkUrl) {
  if (!gameDetail) return;

  const badge = document.createElement("span");
  badge.className = "game-badge";
  badge.textContent = "JJ";

  const details = document.createElement("div");
  const heading = document.createElement("strong");
  heading.textContent = title;
  const caption = document.createElement("small");
  caption.textContent = description;
  details.append(heading, caption);

  if (linkUrl) {
    const link = document.createElement("a");
    link.className = "game-fixture-link";
    link.href = linkUrl;
    link.target = "_blank";
    link.rel = "noreferrer";
    link.textContent = "Official fixture details ↗";
    details.append(link);
  }

  gameDetail.replaceChildren(badge, details);
}

// fixtures.json comes first: it is published with the site (rebuilt from the official schedule every
// few hours), so it loads instantly even while a free-plan API is still waking up. The API is the
// fallback, for example when running locally where fixtures.json is not generated.
async function fetchUpcomingGames() {
  try {
    const response = await fetch("fixtures.json", { cache: "no-cache" });
    if (response.ok) {
      // The file can be a few hours old, so drop games that have already been played (Hobart date).
      const today = new Intl.DateTimeFormat("en-CA", { timeZone: "Australia/Hobart" }).format(new Date());
      const games = await response.json();
      if (Array.isArray(games)) return games.filter((game) => game.gameDate >= today);
    }
  } catch {
    // Fall through to the API.
  }

  if (!enquiriesOpen) {
    throw new Error("The official fixture feed is unavailable.");
  }

  const response = await fetch(`${apiBase}/api/games`);
  if (!response.ok) {
    throw new Error("The official fixture feed is unavailable.");
  }
  return response.json();
}

async function loadUpcomingGames() {
  if (!gameChoice) return;

  try {
    const games = await fetchUpcomingGames();
    gameChoice.replaceChildren();

    games.forEach((game) => {
      const option = document.createElement("option");
      option.value = `${game.displayDate} - JackJumpers vs ${game.opponent} - ${game.tipoff}`;
      option.textContent = `${game.displayDate} · vs ${game.opponent} · ${game.tipoff}`;
      option.dataset.opponent = game.opponent;
      option.dataset.date = game.displayDate;
      option.dataset.tipoff = game.tipoff;
      option.dataset.url = game.officialUrl;
      gameChoice.append(option);
    });

    const futureOption = document.createElement("option");
    futureOption.value = "future";
    futureOption.textContent = "Ask about another home game";
    gameChoice.append(futureOption);

    gameChoice.addEventListener("change", showGameDetail);
    if (games.length) {
      showGameDetail();
    } else {
      gameChoice.value = "future";
      setGameDetail(
        "No upcoming home games are listed",
        "Check the official fixture for the latest schedule.",
        "https://www.jackjumpers.com.au/schedule"
      );
    }
  } catch {
    gameChoice.replaceChildren(new Option("Ask us about an upcoming home game", "future"));
    gameChoice.addEventListener("change", showGameDetail);
    setGameDetail(
      "Live fixtures are temporarily unavailable",
      "You can still enquire about an upcoming home game or check the official schedule.",
      "https://www.jackjumpers.com.au/schedule"
    );
  }
}

loadUpcomingGames();

// A free-plan API sleeps when idle and takes up to a minute to start. Wake it as soon as the page
// opens, so it is usually ready by the time a guest sends the form.
if (enquiriesOpen) {
  fetch(`${apiBase}/api/health`, { cache: "no-store" }).catch(() => {});
}

if (emailEnquiries && bookingForm) {
  formStatus.textContent = "Sending opens your email app with your enquiry ready to go — just press Send.";
} else if (!enquiriesOpen && bookingForm) {
  bookingForm.querySelectorAll("input, textarea, button").forEach((control) => {
    control.disabled = true;
  });
  formStatus.textContent = "Online enquiries open soon. Please check back shortly.";
}

// Builds a mailto: link, so the enquiry is sent from the guest's own email account.
function sendEnquiryByEmail() {
  const match = gameChoice.value === "future"
    ? "Another home game"
    : gameChoice.selectedOptions[0]?.textContent || gameChoice.value;
  const note = guestNote.value.trim();
  const body = [
    "Hello Boxwood team,",
    "",
    "I'd like to enquire about the Stay & Play game-night package.",
    "",
    `Name: ${guestName.value.trim()}`,
    `Email: ${guestEmail.value.trim()}`,
    `Match: ${match}`,
    `Preferences: ${note || "None"}`,
    "",
    "Thank you",
  ].join("\n");
  const subject = `Stay & Play enquiry – ${guestName.value.trim()}`;
  window.location.href = `mailto:${encodeURIComponent(enquiryEmail)}?subject=${encodeURIComponent(subject)}&body=${encodeURIComponent(body)}`;
  formStatus.textContent = `Your email app should now open with the enquiry ready. Press Send to reach us. If nothing opened, email ${enquiryEmail}.`;
}

function setFieldError(input, errorElement, message) {
  input.setAttribute("aria-invalid", String(Boolean(message)));
  errorElement.textContent = message;
}

function validateBookingForm() {
  let valid = true;
  const nameError = document.querySelector("#guest-name-error");
  const emailError = document.querySelector("#guest-email-error");
  const name = guestName.value.trim();

  setFieldError(guestName, nameError, "");
  setFieldError(guestEmail, emailError, "");

  if (name.length < 2) {
    setFieldError(guestName, nameError, "Please enter at least 2 characters.");
    valid = false;
  }

  if (!guestEmail.validity.valid) {
    setFieldError(guestEmail, emailError, "Please enter a valid email address.");
    valid = false;
  }

  return valid;
}

guestNote?.addEventListener("input", () => {
  noteCount.textContent = String(guestNote.value.length);
});

bookingForm?.addEventListener("submit", async (event) => {
  event.preventDefault();
  formStatus.classList.remove("is-error");
  formStatus.textContent = "";

  if (!validateBookingForm()) {
    formStatus.classList.add("is-error");
    formStatus.textContent = "Please check the highlighted details.";
    return;
  }

  if (!gameChoice?.value) {
    formStatus.classList.add("is-error");
    formStatus.textContent = "Please wait for the official fixtures to load before sending your enquiry.";
    return;
  }

  if (emailEnquiries) {
    // Bots that fill the hidden field get nothing; people get their mail app.
    if (!guestWebsite?.value) sendEnquiryByEmail();
    return;
  }

  const now = Date.now();
  if (now - clientRateLimit.lastSubmission < clientRateLimit.cooldownMs) {
    formStatus.classList.add("is-error");
    formStatus.textContent = "Please wait a few seconds before trying again.";
    return;
  }

  clientRateLimit.lastSubmission = now;
  const submitButton = bookingForm.querySelector("button[type=submit]");
  submitButton.disabled = true;
  submitButton.classList.add("is-loading");
  formStatus.textContent = "Sending your private enquiry…";
  const slowNotice = window.setTimeout(() => {
    formStatus.textContent = "Still sending — the booking service is starting up, which can take up to a minute. Please keep this page open.";
  }, 6000);

  try {
    const response = await fetch(`${apiBase}/api/enquiries`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({
        guestName: guestName.value.trim(),
        guestEmail: guestEmail.value.trim(),
        guestNote: guestNote.value.trim(),
        gameChoice: gameChoice?.value || "next",
        website: guestWebsite?.value || "",
      }),
    });

    if (!response.ok) {
      const error = await response.json().catch(() => ({}));
      const fieldError = error.errors && Object.values(error.errors).flat()[0];
      if (response.status === 429) {
        throw new Error(error.detail || "Too many attempts. Please wait a minute and try again.");
      }
      throw new Error(fieldError || error.detail || "The enquiry could not be sent.");
    }

    formStatus.textContent = "Thank you — your enquiry has been received. The Boxwood team will be in touch.";
    bookingForm.reset();
    noteCount.textContent = "0";
  } catch (error) {
    clientRateLimit.lastSubmission = 0;
    formStatus.classList.add("is-error");
    formStatus.textContent = error.message.includes("Failed to fetch")
      ? "We couldn't reach the booking service. Please try again in a few minutes."
      : error.message;
    submitButton.disabled = false;
  } finally {
    window.clearTimeout(slowNotice);
    submitButton.classList.remove("is-loading");
  }
});
