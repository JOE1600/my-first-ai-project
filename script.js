const menuToggle = document.querySelector(".menu-toggle");
const nav = document.querySelector("nav");
const reserveButton = document.querySelector("#reserve-button");
const reserveMessage = document.querySelector("#reserve-message");
const bookingForm = document.querySelector("#booking-form");
const guestName = document.querySelector("#guest-name");
const guestEmail = document.querySelector("#guest-email");
const guestNote = document.querySelector("#guest-note");
const formStatus = document.querySelector("#form-status");
const noteCount = document.querySelector("#note-count");

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

window.setTimeout(() => {
  document.querySelector(".hero-copy")?.classList.add("is-visible");
}, 120);

document.querySelectorAll(".spotlight-card").forEach((card) => {
  card.addEventListener("pointermove", (event) => {
    const bounds = card.getBoundingClientRect();
    const x = event.clientX - bounds.left;
    const y = event.clientY - bounds.top;
    const tiltX = ((x / bounds.width) - 0.5) * 3;
    const tiltY = ((y / bounds.height) - 0.5) * -3;
    card.style.setProperty("--spot-x", `${x}px`);
    card.style.setProperty("--spot-y", `${y}px`);
    card.style.setProperty("--tilt-x", `${tiltX}deg`);
    card.style.setProperty("--tilt-y", `${tiltY}deg`);
  });

  card.addEventListener("pointerleave", () => {
    card.style.removeProperty("--tilt-x");
    card.style.removeProperty("--tilt-y");
  });
});

reserveButton?.addEventListener("click", () => {
  reserveMessage.textContent = "Thank you — our Boxwood team will be in touch to confirm your game night.";
  reserveButton.textContent = "Request received ✓";
  reserveButton.disabled = true;
});

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

bookingForm?.addEventListener("submit", (event) => {
  event.preventDefault();
  formStatus.classList.remove("is-error");
  formStatus.textContent = "";

  if (!validateBookingForm()) {
    formStatus.classList.add("is-error");
    formStatus.textContent = "Please check the highlighted details.";
    return;
  }

  const now = Date.now();
  if (now - clientRateLimit.lastSubmission < clientRateLimit.cooldownMs) {
    formStatus.classList.add("is-error");
    formStatus.textContent = "Please wait a few seconds before trying again.";
    return;
  }

  clientRateLimit.lastSubmission = now;
  formStatus.textContent = "Enquiry saved for this demo. A secure booking service would submit it here.";
  bookingForm.querySelector("button[type=submit]").disabled = true;
});
